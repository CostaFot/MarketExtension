# Market Extension for Command Palette — Claude Guide

## Documentation

- [Extension overview & concepts](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extensions-overview)
- [Creating an extension (getting started guide)](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/creating-an-extension)
- [Toolkit namespace — full class list](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/microsoft-commandpalette-extensions-toolkit)
- [Command results](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/command-results)
- [Sample extensions](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/samples)
- [Adding Dock support](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support) — for the future ticker phase; full writeup + worked examples in [`reference/dock-support.md`](reference/dock-support.md)

## Build & Deploy

Build via Visual Studio or:
```
dotnet build MarketExtension.sln
```
Deploy the MSIX package, then reload Command Palette to pick up changes.

> Debug builds are clean. A **Release** build on an ARM64 box needs a pinned RID, e.g.
> `dotnet build MarketExtension/MarketExtension.csproj -c Release -r win-arm64` (the release
> pipeline pins RIDs already, so this only matters for local Release builds).

## Reference Library (`reference/`)

Real-world implementations from the **AdbExtension** this project was scaffolded from live
in `reference/` — **not compiled** (outside the project folder). Consult them before
writing a new command or page; copy and adapt rather than reinventing. The live code
(`Pages/SearchPage.cs`, `Pages/PricedListPage.cs`, `Pages/FavoritesDockPage.cs`,
`Helpers/ProcessHelper.cs`) shows these patterns in use. See `reference/README.md` for the full index;
the highest-value examples:

| Example | Demonstrates |
|---|---|
| `reference/pages/AdbExtensionPage.cs` | `DynamicListPage` + async load + the `INotifyItemsChanged` on-load refresh, sections, nested command |
| `reference/pages/PackageActionsPage.cs` | `ListPage` (sync `GetItems`) + `INotifyItemsChanged` fire-on-subscribe variant, per-item action menu |
| `reference/commands/TakeScreenshotCommand.cs` | external process exec, pull a file, success/error toasts, `Win32` errno-2 handling |
| `reference/settings/AdbSettingsManager.cs` | `JsonSettingsManager` singleton + a "keep open" success-toast helper |
| `reference/dock-support.md` | **Dock band** how-to for the future ticker: `GetDockBands()`, live-update lifecycle, and pointers to the Performance Monitor + MediaControls extensions on disk |

## Project Conventions

- New commands → `MarketExtension/Commands/`, extend `InvokableCommand`
- New pages → `MarketExtension/Pages/`, extend `ListPage` or `DynamicListPage`
- All external process execution goes through `ProcessHelper.Run()` — never use `Process` directly in command files
- Error toasts use `CommandResult.KeepOpen()` so the user can read them; one-shot success toasts use the
  default `Dismiss()`. Exception: in-place list mutations (watchlist/favorites add/remove — see
  `Commands/MembershipCommands.cs`) toast **and** `KeepOpen()`, so the user gets explicit confirmation and can keep editing
- Icons: `new IconInfo("https://github.com/favicon.ico")` per project preference

## Market Data Architecture (the core of this app)

Live quotes flow through three explicitly-named layers + a coordinator. **Keep this layering and
naming for new code.**

```
ApiFinnhubQuoteDto ─(provider maps)→ DomainQuote ─(repository routes+merges)→ IReadOnlyList<DomainQuote>
                                                          └─(page maps via UiQuote.From)→ UiQuote (rendered)
```

**Naming convention:**
- `Api*` — raw provider DTOs (provider-specific), e.g. `ApiFinnhubQuoteDto`.
- `Domain*` — provider-agnostic, **no formatting**; what every provider AND the repository return (`DomainQuote`, `DomainInstrument`).
- `Ui*` — presentation; the ONLY place `FormatPrice()`/`FormatChange()` live (`UiQuote`).
- `AssetCategory` (enum) stays **unprefixed** — shared vocabulary across all layers.

**File map:**

| File | Role |
|---|---|
| `Data/MarketRepository.cs` | **The coordinator the UI depends on.** Routes each `DomainInstrument` to the first `IMarketDataProvider` whose `Supports(AssetCategory)` matches, fans out concurrently, merges into one order-preserving list. Also `SearchAsync(query)` — fans out the free-text lookup to every provider and merges/dedupes by symbol. No provider for a category → `IsValid:false` placeholder. |
| `Data/IMarketDataProvider.cs` | ONE data source: `bool Supports(AssetCategory)` + `GetQuotesAsync(instruments, ct)` + `SearchAsync(query, ct)` (free-text symbol lookup → `DomainInstrument`s, **identity only, no prices**; a provider that can't search returns `[]`). |
| `Data/Finnhub/FinnhubMarketDataProvider.cs` | Active provider (`Supports` Stock+Crypto). Maps `ApiFinnhubQuoteDto` → `DomainQuote`; `SearchAsync` calls `/search` (US equities only — see Finnhub specifics). |
| `Data/MockMarketDataProvider.cs` | Offline fallback (`Supports` all). `SearchAsync` filters its seed keys. |
| `Data/InstrumentCatalog.cs` | Static `DomainInstrument` defaults — the **first-run seed** for `WatchlistStore` (no longer always-shown; removable once seeded). |
| `Settings/WatchlistStore.cs` | JSON-persisted tracked instruments, each carrying **two independent flags** `InWatchlist`/`IsFavorite` (favorites = the dock subset). Stores **full `DomainInstrument` identity** so searched non-catalog symbols re-price; an entry with both flags false is dropped. Source-gen `WatchlistItem`/`WatchlistJsonContext` → `market_watchlist.json`; seeds `InstrumentCatalog` on first run, else migrates the legacy `market_favorites.json` (old pins → watchlisted **and** favorited). Pages read its `Watchlist`/`Favorites` subsets directly. Replaces the old `FavoritesStore` + `Watchlist.cs`. |
| `Data/Finnhub/ApiFinnhubQuoteDto.cs` | Raw `/quote` DTO + the **single** `FinnhubJsonContext` (all `[JsonSerializable]` live here — see AOT/trim gotcha). |
| `Data/Finnhub/ApiFinnhubSearchDto.cs` | Raw `/search` DTOs (`ApiFinnhubSearchDto` / `...ResultDto`); registered on `FinnhubJsonContext` in the quote file. |
| `Models/{AssetCategory,DomainInstrument,DomainQuote,UiQuote}.cs` | the model layers |

**To add a provider** (e.g. forex): implement `IMarketDataProvider` (`Supports`, `GetQuotesAsync`,
and `SearchAsync` — return `[]` if it can't search), map its `Api*` DTO → `DomainQuote`, and register
it in `MarketExtensionCommandsProvider`:
`new MarketRepository(new FinnhubMarketDataProvider(), new YourProvider())`. **Zero UI changes.**

**Finnhub specifics:**
- Base `https://finnhub.io/api/v1`, endpoint `/quote`. Free tier ~60 calls/min, ~300/day.
- **No forex on the free tier** — `OANDA:*` returns 403. FX is omitted from `InstrumentCatalog`;
  re-add via a paid plan or a keyless FX provider (e.g. Frankfurter) behind the same seam.
- Symbol formats: stock = bare (`AAPL`), crypto = `BINANCE:{SYM}USDT`, FX = `OANDA:{BASE}_{QUOTE}`.
- An all-zero `/quote` response = invalid/unknown symbol → map to `IsValid:false`.
- **Symbol search** `/search?q=&exchange=US` (free tier): returns identity only (`symbol`/`description`/
  `type`), **no prices**. `symbol` is the canonical id for `/quote`; `displaySymbol` is for UI. Scoped to
  **US equities** and mapped to `AssetCategory.Stock` — Finnhub's canonical `symbol` only round-trips
  through `ToFinnhubSymbol(Stock)` for plain US tickers; crypto/FX search needs symbol-format
  reconciliation (deferred). UI is **Enter-only** (the synthetic "Search Finnhub for …" item), never
  per-keystroke, to protect the rate limit. A name can list across exchanges → dedupe by symbol.

**AOT/trim:** project is AOT/trim-enabled; `EnableTrimAnalyzer` is on **in Debug** (so IL2026/IL3050
surface every build) and `ILLinkTreatWarningsAsErrors` is set, so all JSON must go through a source-gen
`JsonSerializerContext` (never reflection-based `JsonSerializer`). Both contexts are source-gen:
`FinnhubJsonContext` (quotes + search) and `WatchlistJsonContext` (watchlist items + legacy migration).
⚠️ **Gotcha:** the JSON source generator does **not** support `[JsonSerializable]` attributes split
across multiple `partial` declarations of one context — it emits a colliding hintName (e.g.
`FinnhubJsonContext.Decimal.g.cs`) and the *whole* generator silently fails, cascading CS0534
("does not implement … `GetTypeInfo`") onto **every** context in the build. Keep all `[JsonSerializable]`
for a given context on a **single** declaration (see `ApiFinnhubQuoteDto.cs`).

## API Key (secrets.props — the .NET "local.properties")

- The Finnhub key lives in **gitignored** `MarketExtension/secrets.props`. Fresh clone: copy
  `MarketExtension/secrets.props.template` → `secrets.props` and paste the key.
- The csproj `GenerateSecrets` target bakes it into a generated `Secrets.FinnhubApiKey` const (the
  BuildConfig analog); `FinnhubMarketDataProvider` reads that. Missing file → empty key, build still succeeds.
- Caveat: the key is compiled into the shipped MSIX (extractable). True per-user secrecy = the
  deferred bring-your-own-key in Command Palette settings.

## Logging

- `Log.Info/Warn/Error(tag, message)` (`Helpers/Log.cs`) — tagged by component (`Finnhub`,
  `Repository`, `ComServer`, `Startup`).
- `Info`/`Warn` are `[Conditional("DEBUG")]` → **compiled out of Release** (the MSIX ships silent).
  `Error` also reports to **Sentry**, which survives into Release (no-op until a DSN is set in `Program.cs`).
- Watch live (Debug builds): VS **Debug → Attach to Process → `MarketExtension.exe`** (Managed) →
  Output window; or Sysinternals **DebugView** ("Capture Global Win32"). **NEVER log the API token.**

## Current Status / Next Steps

- **Done:** layered data architecture; live Finnhub provider; `MarketRepository` coordinator;
  `secrets.props` key handling; tagged logging; **Enter-only Finnhub `/search`**; **persistent
  watchlist + favorites** as two independent flags (`WatchlistStore`); and the **three-screen UX**
  (Markets Search / Watchlist / Favorites — see below).
  ⚠️ Working tree is **uncommitted** (on `repo`).
- **Next up:** **live price polling** — auto-refresh prices on a timer while a surface is visible,
  **default 60 s, configurable in settings**; and **pushing dock refreshes when favorites change** so a
  pinned band updates immediately instead of going stale (both designed below).
- **Deferred:** FX provider (keyless Frankfurter); settings UI for bring-your-own-key; rate-limit (429)
  + richer error UX; crypto/FX in symbol search.
- **Wishlist:** a **portfolio** screen + its own dock band (holdings, total value, daily P&L); and a
  **per-symbol detail screen + live chart for any ticker** — drill-in from any list row and clickable
  from any dock item (Performance-Monitor-style). Both designed below.

### Three-screen UX (done)

Three top-level commands in `MarketExtensionCommandsProvider.TopLevelCommands()`, backed by **two
independent membership flags** per instrument in `WatchlistStore` — an instrument can be on either
list, both, or neither (so "watchlist" and "favorites" are genuinely separate sets, not subset/superset).
All three command titles share the **`Markets ` prefix** so they group together (and don't pollute the
namespace) when searching the Command Palette root:

1. **Markets Search** (`Pages/SearchPage.cs`, default entry) — the Enter-only `/search` flow. On a
   result: **Enter** → add to watchlist; **Ctrl+Enter** (the first `MoreCommands` context item, the
   toolkit's secondary-activation slot) → add to favorites. Empty box links to the other two screens.
2. **Markets Watchlist** (`Pages/WatchlistPage.cs`) — the tracked instruments, priced and grouped by
   class; a ★ marks favorites. **Enter** → remove from watchlist; **Ctrl+Enter** → toggle favorite.
3. **Markets Favorites** (`Pages/FavoritesPage.cs`) — the curated subset; **only favorites render in
   the dock band** (`FavoritesDockPage` prices `WatchlistStore.Favorites`). **Enter** → remove from
   favorites. (Caveat: a pinned band only re-reads when it reopens — pushing live refreshes on change
   is a next step, see below.)

Watchlist + Favorites share `Pages/PricedListPage.cs` (async price + the on-load `INotifyItemsChanged`
refresh + local filter). The catalog seeds the watchlist **once** on first run; thereafter every row
is user-removable. The membership actions live in `Commands/MembershipCommands.cs`
(`AddToWatchlist`/`RemoveFromWatchlist`/`AddToFavorites`/`RemoveFromFavorites`/`ToggleFavorite`) — each
**shows a confirmation toast** (e.g. "Added AAPL to watchlist") and **keeps the palette open** so the
user gets explicit feedback and can keep editing (the silent re-list alone was too ambiguous).

### Live price polling (next — designed, not built)

Today every priced surface fetches **once** when it becomes visible (the on-load `INotifyItemsChanged`
refresh). The next iteration auto-refreshes on a timer while a surface is visible: **default 60 s,
configurable in settings** (including an "Off" choice).

- **Where:** `Pages/PricedListPage.cs` (covers Markets Watchlist + Markets Favorites) and
  `Pages/FavoritesDockPage.cs`. `SearchPage` is exempt — its results are identity-only, no prices.
- **Lifecycle = the event we already have.** The `INotifyItemsChanged.ItemsChanged` `add`/`remove`
  accessors are de-facto `Loaded()`/`Unloaded()` hooks (CmdPal subscribes when a surface is visible,
  unsubscribes when hidden — see `reference/dock-support.md`). Today `add` does the one-shot fetch;
  extend it to also start a poll loop and stop that loop in `remove`. This is the
  `PerformanceWidgetsPage` pattern: Loaded → start timer; tick → re-price + `RaiseItemsChanged()`;
  Unloaded → cancel. Guard a shared source with a refcount if a page and the dock can be live at once.
- **Mechanism:** a `PeriodicTimer` in an async loop guarded by a `CancellationTokenSource` created in
  `add` and cancelled/disposed in `remove`. Re-read the interval from settings each tick (so changes
  apply without a reload) and **skip a tick if the previous fetch is still in flight** (no overlapping
  calls). The on-load fetch stays as the immediate first paint; the timer drives the rest.
- **Settings:** add `Settings/MarketSettingsManager.cs` — a `JsonSettingsManager` singleton modeled on
  `reference/settings/AdbSettingsManager.cs` (`FilePath` = `…/market.settings.json`, `Settings.Add(...)`,
  `LoadSettings()`, `Settings.SettingsChanged += SaveSettings`). Expose a refresh-interval setting (a
  `ChoiceSetSetting` of presets — `Off / 30 s / 60 s / 5 m` — or a numeric `TextSetting` defaulting to
  `60`). Wire it in `MarketExtensionCommandsProvider` via `Settings = MarketSettingsManager.Instance.Settings;`
  (the commented-out hook is already there). The poll loop reads `MarketSettingsManager.Instance.RefreshIntervalSeconds`.
- ⚠️ **Rate-limit tension (design around this):** Finnhub free tier is **~60 calls/min AND ~300/day**,
  and `GetQuotesAsync` issues **one `/quote` call per instrument**. Polling N instruments every 60 s = N
  calls/min — e.g. 6 favorites = 360 calls/hour, which **exhausts the ~300/day budget in under an hour**.
  60 s is fine for short sessions but not sustained use. Mitigations to ship alongside: the configurable
  interval + an **Off** escape, polling **only while visible** (the lifecycle guarantees this), and real
  **429 handling** (back off; surface a "rate-limited" state rather than blanking prices). A
  batched-quote provider or cheaper data source would relax this later.

### Dock refresh on favorites change (next — designed, not built)

`FavoritesDockPage` only re-reads favorites + prices when it becomes **visible** (its on-load
`INotifyItemsChanged` hook). So adding/removing a favorite from a palette page does **not** update a
band that's already pinned and showing — it stays stale until the dock re-opens (or, once polling lands,
until the next tick). To update it the instant a favorite changes:

- Make `WatchlistStore` **observable**: raise a `FavoritesChanged` event whenever a favorite flag flips
  (`AddToFavorites` / `RemoveFromFavorites`, and the first-run migration). Keep it lightweight (no args —
  listeners just re-read `Favorites`).
- `FavoritesDockPage` **subscribes in its `add` accessor and unsubscribes in `remove`** (the same
  Loaded/Unloaded lifecycle as polling), calling `RefreshQuotes()` on the event. Subscribing only while
  visible keeps a hidden band from doing work and avoids leaks.
- Complementary to polling, not redundant: polling catches *price* drift on a timer; this pushes
  *membership* changes immediately, and still works when polling is **Off**.

### Portfolio screen + dock band (future wishlist)

A fourth top-level screen (**Markets Portfolio**) plus its own dock band, tracking actual *holdings*
(not just symbols): total market value and **daily P&L**. Reuses the existing data seam with **no
provider changes** — `MarketRepository.GetQuotesAsync` already returns per-instrument daily change.

**Data model** (keep the `Api`/`Domain`/`Ui` layering; formatting only in `Ui*`):
- `Settings/PortfolioStore.cs` — JSON-persisted holdings, modeled on `WatchlistStore` (singleton, lock,
  source-gen `PortfolioJsonContext` → `market_portfolio.json`). Each entry = symbol + name + category +
  **quantity** (+ optional **cost basis** for total/unrealized P&L later); quantity is `decimal` so
  crypto fractions work.
- `Models/DomainPosition.cs` — a holding's provider-agnostic identity (`DomainInstrument` + quantity +
  optional cost basis), **no prices**.
- `Models/UiPosition.cs` + `UiPortfolio.cs` — presentation projection: combine a `DomainQuote` with the
  quantity to expose `MarketValue` (qty × price), `DailyPnL` (qty × `DomainQuote.Change`),
  `DailyPnLPercent`, and the `Format*()` helpers (the only place formatting lives, like `UiQuote`);
  `UiPortfolio` rolls up the totals.

**Daily P&L:** per position = `Quantity × DomainQuote.Change` (Finnhub `/quote` `d` = today's per-unit
change, `dp` = its percent). Portfolio total value = Σ market value; total daily P&L = Σ daily P&L; the
aggregate **percent is `totalDailyPnL / (totalValue − totalDailyPnL)`** (vs. yesterday's close) — not an
average of the per-position percents.

**Screen** (`Pages/PortfolioPage.cs`): reuse `PricedListPage`'s async price/refresh plumbing (price the
holdings via the repository, zip with quantities). Render a **totals summary** as the first item
("Portfolio $12,345.67  ▲ +$120.50 (+0.98%)") then one row per holding ("AAPL · 10 sh" → value + daily
P&L). Editing needs a quantity-input affordance the palette doesn't give for free — add via search → a
small "set quantity" form/content page; Enter on a row to edit/remove. Like every other list, a row also
opens the **shared symbol detail + chart** (below) — here via a context item, since Enter is taken by edit.

**Dock band** (`Pages/PortfolioDockPage.cs`): a second band registered in `GetDockBands()` next to the
favorites band, showing the one-line portfolio summary (total value + daily P&L, green/red). Needs its
own **non-empty `Command.Id`** (`com.costafotiadis.market.dock.portfolio` — see `reference/dock-support.md`)
and inherits the same live-refresh story as the favorites band (the polling + push-on-change work above).

**Caveats:** assumes a single quote currency (USD); mixed-currency holdings need FX conversion (deferred —
ties to the FX-provider item). Exclude `IsValid:false` quotes from totals (or show them as unpriced).

### Symbol detail + live chart — for ANY instrument (future wishlist)

**App-wide, not portfolio-specific.** Every instrument — from search results, the watchlist, favorites,
**and** portfolio rows, plus **every dock band** — should open a per-symbol **detail screen with a small
live line chart** of its price (the way Performance Monitor's bands open a CPU/RAM graph). **One** shared
`SymbolDetailPage` backs both the **drill-in (Enter, or a context item, on any row)** and the
**dock-item click**. Investigated against its on-disk source
(`C:\Users\jarla\code\PowerToys\src\modules\cmdpal\ext\Microsoft.CmdPal.Ext.PerformanceMonitor\`) — the
mechanism is simpler than it looks:

**How Performance Monitor does it (the pattern to copy):**
- A band button's `Command` is itself an **`IContentPage`** (`WidgetPage : OnLoadContentPage`), so
  clicking the button **navigates into** the page. Same trick works for a list row — point the row's
  command at the content page.
- The page renders an **Adaptive Card** via a `FormContent` (`TemplateJson` = an adaptive-card template +
  `DataJson` = bound values); `GetContent()` returns `[formContent]`.
- **The chart is an inline SVG embedded as a data URI** — `ChartHelper.CreateImageUrl(values, type)`
  returns `"data:image/svg+xml;utf8," + <svg>…</svg>`, where the `<svg>` is just two `<polyline>`s (the
  line + a gradient-fill loop) and a border `<rect>`, built with `System.Xml.Linq`. **No
  System.Drawing/SkiaSharp** → AOT/trim-safe (this project requires that — see the AOT section). The card
  template binds that data-URI as an `Image`. `MaxChartValues = 34` is a rolling window of the last 34 samples.
- Live update reuses the same Loaded/Unloaded lifecycle as our pages: `OnLoadBasePage` turns the
  `ItemsChanged` add/remove into `Loaded()`/`Unloaded()`; each data tick re-renders `DataJson` and calls
  `RaiseItemsChanged()` so the card repaints. A `PushActivate`/`PopActivate` refcount keeps the source
  alive while either the band button or the open chart needs it.

**Where to look (exact files):**
- `…/PerformanceMonitor/PerformanceWidgetsPage.cs` — `WidgetPage : OnLoadContentPage`, `GetContent()` →
  `FormContent`; `LoadContentData()` sets `cpuGraphUrl = CreateCPUImageUrl()`; the `Updated →
  RaiseItemsChanged` repaint; the `PushActivate`/`PopActivate` refcount.
- `…/PerformanceMonitor/OnLoadStaticPage.cs` — `OnLoadContentPage` / `OnLoadBasePage`: the `ItemsChanged`
  add/remove ⇒ `Loaded()`/`Unloaded()` (the generalized form of our on-load hook).
- `…/PerformanceMonitor/DevHome/Helpers/ChartHelper.cs` — **the file to port**: `CreateImageUrl` →
  `CreateChart(List<float>, type)` building the SVG polyline/rect; `ChartHeight`/`ChartWidth`, `MaxChartValues`.
- `…/PerformanceMonitor/DevHome/Helpers/CPUStats.cs` — `CreateCPUImageUrl()` + the `CpuChartValues`
  rolling `List<float>` buffer (the sampling model).
- `…/PerformanceMonitor/DevHome/Templates/*.json` — the adaptive-card templates that place the
  `${…graphUrl}` image; copy the shape.
- MediaControls `…/Pages/DockHeadItem.cs` — the event-driven content-band variant.

**For market data specifically:**
- Data source = **sample the price into a rolling per-symbol buffer each poll tick** (exactly Perf
  Monitor's model — it samples a live metric, it does not fetch history). So this **depends on the live
  polling work above**; a paid `/stock/candle` (premium-gated on the free tier) could backfill real history
  later. Keep the buffer where the cache/sampler lives (repository-side).
- Add **one** `Pages/SymbolDetailPage.cs` (`IContentPage`) that renders the SVG sparkline (plus
  open/high/low/prev-close, already in `/quote`) for any `DomainInstrument`, recolored green/red by day
  direction. Wire **every** surface to it: **Enter or a context item on rows** in Search, Watchlist,
  Favorites, and Portfolio, **and the click target of every dock button** (favorites dock + portfolio
  dock). It is the single symbol-detail screen, shared everywhere — explicitly not a portfolio feature.
- Port `ChartHelper` mostly as-is (already pure-string SVG); feed it our `UiQuote`/price series.

## CommandPalette Toolkit — Quick Reference

### Navigation

`Page` extends `Command` (implements `ICommand`). Pass a page anywhere a command is accepted — the palette navigates into it automatically. No string-based registration needed.

```csharp
// Top-level entry point
new CommandItem(new MyPage()) { Title = "My Command" }

// List item that navigates into a sub-page
new ListItem(new MySubPage(arg)) { Title = "Go deeper" }

// List item that runs a command
new ListItem(new MyInvokableCommand()) { Title = "Do thing" }
```

### Base Classes

| Class | Use when |
|---|---|
| `InvokableCommand` | Action with no UI — runs and returns a `CommandResult` |
| `ListPage` | Static list of items |
| `DynamicListPage` | List that reacts to search input — override `UpdateSearchText()` |
| `CommandProvider` | Extension entry point — returns top-level `ICommandItem[]` |

### Page Activation Hook (On-Load Refresh)

The framework calls `FetchItems` → `GetItems()` **before** subscribing to `ItemsChanged`. This means any `RaiseItemsChanged` fired from the constructor is lost and the page shows empty on first open.

The fix: re-implement `INotifyItemsChanged` on the page class and intercept the `add` accessor. The framework subscribes via `INotifyItemsChanged.ItemsChanged +=`, so our accessor fires right after subscription — triggering a second `FetchItems` while the framework is already listening.

**Critical:** use `INotifyItemsChanged.ItemsChanged`, not `IListPage.ItemsChanged` — `ItemsChanged` lives on `INotifyItemsChanged`, and the class must re-list the interface to override base class dispatch.

```csharp
// For pages that load data async (see Pages/PricedListPage.cs, reference/pages/AdbExtensionPage.cs):
internal sealed partial class MyPage : DynamicListPage, INotifyItemsChanged
{
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshData(); } // called every time user navigates to this page
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
}

// For pages with synchronous GetItems() (see reference/pages/PackageActionsPage.cs):
internal sealed partial class MyPage : ListPage, INotifyItemsChanged
{
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(-1)); } // fire immediately after subscribe
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
}
```

Do **not** call `IsLoading = true` + `Task.Run(Load)` from the constructor — the event fires before the framework subscribes and the signal is lost.

### DynamicListPage Pattern

```csharp
internal sealed partial class MyPage : DynamicListPage
{
    public MyPage()
    {
        PlaceholderText = "Type to filter...";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0); // triggers GetItems() re-call

    public override IListItem[] GetItems()
    {
        // Filter using this.SearchText
    }
}
```

### CommandResult Options

```csharp
CommandResult.ShowToast("message")              // show toast, dismiss palette
CommandResult.ShowToast(new ToastArgs {
    Message = "message",
    Result = CommandResult.KeepOpen()           // show toast, keep palette open
})
CommandResult.KeepOpen()                        // do nothing, stay on current page
CommandResult.Dismiss()                         // close the palette
CommandResult.GoBack()                          // navigate to previous page
CommandResult.GoHome()                          // navigate to root
CommandResult.Confirm(new ConfirmationArgs())   // show confirmation dialog
```

### ListItem Properties

```csharp
new ListItem(command)
{
    Title = "Required",
    Subtitle = "Optional secondary text",
    Icon = new IconInfo("https://...") ,        // URL icon
    Section = "Section header",                 // groups items under a header
}
```

### Icon Options

```csharp
new IconInfo("https://example.com/icon.png")   // URL (light+dark same)
new IconInfo(lightIconData, darkIconData)       // separate light/dark
IconHelpers.FromRelativePath("Assets\\foo.png") // bundled asset
```

### InvokableCommand Pattern

```csharp
internal sealed partial class MyCommand : InvokableCommand
{
    public MyCommand()
    {
        Name = "Do Thing";
        Icon = new IconInfo("https://...");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            // do work
            return CommandResult.ShowToast("Done");
        }
        catch (Exception ex) when (ex is Win32Exception w && w.NativeErrorCode == 2)
        {
            return ErrorToast("Required tool not found. Make sure it is on your PATH.");
        }
        catch (Exception ex)
        {
            return ErrorToast($"Unexpected error: {ex.Message}");
        }
    }

    private static ICommandResult ErrorToast(string message) =>
        CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
}
```

## ProcessHelper API

```csharp
// Run any external process. Reads BOTH stdout and stderr before WaitForExit (prevents
// pipe deadlocks). stderr is non-empty only on failure (non-zero exit).
ProcessHelper.Run(string fileName, string arguments, out string stdout, out string stderr);
```

For richer process handling (output parsing into records, etc.) see `reference/helpers/AdbHelper.cs`.

## Releasing a New Version

### Step 1 — Bump version in all 5 files

| File | Field |
|---|---|
| `MarketExtension/MarketExtension.csproj` | `<AppxPackageVersion>` |
| `MarketExtension/Package.appxmanifest` | `Identity Version=` |
| `MarketExtension/app.manifest` | `assemblyIdentity version=` |
| `MarketExtension/build-exe.ps1` | `$Version` default param |
| `MarketExtension/setup-template.iss` | `#define AppVersion` |

One-liner (replace `OLD` and `NEW`):
```powershell
$files = @("MarketExtension/MarketExtension.csproj","MarketExtension/Package.appxmanifest","MarketExtension/app.manifest","MarketExtension/build-exe.ps1","MarketExtension/setup-template.iss")
$files | ForEach-Object { (Get-Content $_) -replace 'OLD','NEW' | Set-Content $_ }
```

### Step 2 — Trigger the GitHub Actions build

```
gh workflow run release-msix.yml --ref master -f release_notes="Your release notes here"
```

This reads the version from the csproj automatically, builds x64 + ARM64, bundles into a `.msixbundle`, signs it, and creates a GitHub Release. (`release-extension.yml` is the alternative self-signed `.exe` installer path.)

### Step 3 — Submit to Partner Center

1. Download the `.msixbundle` from the GitHub Release
2. Partner Center → app → new submission → Packages → upload the `.msixbundle`
3. Update description/notes, submit

## One-time setup (this extension's own identity)

This was scaffolded from AdbExtension. Already changed: COM GUID (`6b38c9aa-bbee-45e9-81e9-cf25707910e7`), namespace/assembly (`MarketExtension`), package identity (`CostaFotiadis.MarketExtension`), version (`0.0.1.0`). Still TODO before shipping:

- **Partner Center**: reserve the app name; confirm `Identity Name` + `Publisher` in `Package.appxmanifest`/csproj match what it assigns.
- **GitHub secrets**: add `SIGNING_CERT_PFX` + `SIGNING_CERT_PASSWORD` (see `MarketExtension/create-signing-cert.ps1`). Template clones do not carry secrets.
- **Assets**: replace the art in `MarketExtension/Assets/` (still the AdbExtension tiles/logos).
- **Sentry**: set your own DSN in `Program.cs` or leave it empty to disable.
- **API key**: create `MarketExtension/secrets.props` from `secrets.props.template` and paste your Finnhub key (gitignored; see the "API Key" section above).
- **Description**: update the placeholder description in `Package.appxmanifest`.

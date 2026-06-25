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
- All external process execution goes through `ProcessHelper` (`Run()` for captured CLI calls; `OpenUrl()` to launch a URL/file in the OS default handler) — never use `Process` directly in command files
- Error toasts use `CommandResult.KeepOpen()` so the user can read them; one-shot success toasts use the
  default `Dismiss()`. Exception: in-place list mutations (watchlist/favorites add/remove — see
  `Commands/MembershipCommands.cs`) toast **and** `KeepOpen()`, so the user gets explicit confirmation and can keep editing
- Icons: **instrument** rows / dock buttons / the detail header show the real asset logo via
  `AssetIconResolver.Resolve(...)` (Elbstream CDN — see "Asset logos (done)"), with a per-category Segoe
  glyph fallback; page **chrome** icons (list/search headers) still use
  `new IconInfo("https://github.com/favicon.ico")` per project preference

## Market Data Architecture (the core of this app)

Live quotes flow through three explicitly-named layers + a coordinator. **Keep this layering and
naming for new code.**

```
ApiFinnhubQuoteDto ─(provider maps)→ DomainQuote ─(repository routes+merges)→ IReadOnlyList<DomainQuote>
                                                          └─(page maps via UiQuote.From)→ UiQuote (rendered)

ApiFinnhubCandleDto ─(provider maps)→ DomainCandleSeries ─(repository routes)→ DomainCandleSeries
                                                          └─(page maps via UiCandleSeries.From)→ UiCandleSeries (SVG chart)
```

The **candle/chart history** flow (added for the symbol-detail chart) mirrors the quote flow through
the same three layers and the same provider seam — see the "Symbol detail + live chart" section.

**Naming convention:**
- `Api*` — raw provider DTOs (provider-specific), e.g. `ApiFinnhubQuoteDto`.
- `Domain*` — provider-agnostic, **no formatting**; what every provider AND the repository return (`DomainQuote`, `DomainInstrument`, `DomainCandleSeries`).
- `Ui*` — presentation; the ONLY place `FormatPrice()`/`FormatChange()`/SVG live (`UiQuote`, `UiCandleSeries`).
- `AssetCategory`, `ChartRange`, `CandleInterval` (enums) stay **unprefixed** — shared vocabulary across all layers.

**File map:**

| File | Role |
|---|---|
| `Data/MarketRepository.cs` | **The coordinator the UI depends on.** Routes each `DomainInstrument` to the first `IMarketDataProvider` whose `Supports(AssetCategory)` matches, fans out concurrently, merges into one order-preserving list. Also `SearchAsync(query)` — fans out the free-text lookup to every provider and merges/dedupes by symbol — and `GetCandlesAsync(instrument, range)` — routes the chart history to the first supporting provider (no fan-out; one instrument). No provider for a category → `IsValid:false` quote / invalid candle series. |
| `Data/IMarketDataProvider.cs` | ONE data source: `bool Supports(AssetCategory)` + `GetQuotesAsync(instruments, ct)` + `SearchAsync(query, ct)` (free-text symbol lookup → `DomainInstrument`s, **identity only, no prices**; a provider that can't search returns `[]`) + `GetCandlesAsync(instrument, ChartRange, ct)` (price history for the detail chart; **default interface method** returns an invalid series, so non-candle providers opt out for free). |
| `Data/TwelveData/TwelveDataMarketDataProvider.cs` | **Primary provider when its key is set** (`Supports` Stock+Crypto+Currency, **gated on `HasTwelveDataApiKey`** → first-match routing falls back to Finnhub/Frankfurter when unset). One API for all three classes; **`/time_series` candles are free-tier**, so charts render on a free key. `GetQuotesAsync` **batches all symbols into one `/quote`** call (tight 8/min limit); `SearchAsync` = `/symbol_search` (results normalized back to neutral symbols). See Twelve Data specifics. |
| `Data/Finnhub/FinnhubMarketDataProvider.cs` | Stock+Crypto provider (`Supports` Stock+Crypto), used when no Twelve Data key is set. Maps `ApiFinnhubQuoteDto` → `DomainQuote`; `SearchAsync` calls `/search` (US equities only — see Finnhub specifics). |
| `Data/Frankfurter/FrankfurterMarketDataProvider.cs` | FX provider (`Supports` Currency). **Keyless** ECB daily rates via Frankfurter (`https://api.frankfurter.dev/v1/`). Splits a 6-letter pair (`EURUSD` → base `EUR`/quote `USD`), calls the time-series endpoint for both quotes (latest vs previous fixing = **day-over-day** change) and candles (daily closes). `SearchAsync` = local filter over the catalog's FX pairs (no Frankfurter free-text endpoint exists). See Frankfurter specifics. |
| `Data/Frankfurter/ApiFrankfurterDto.cs` | Raw time-series DTO (`ApiFrankfurterSeriesDto`: `rates` = date→{currency→rate}) **+ the flat `/latest` DTO (`ApiFrankfurterLatestDto`: `rates` = currency→rate)** used by `CurrencyConverter` + the source-gen `FrankfurterJsonContext`. |
| `Data/MockMarketDataProvider.cs` | Offline fallback (`Supports` all). `SearchAsync` filters its seed keys. |
| `Data/InstrumentCatalog.cs` | Static `DomainInstrument` defaults — the **first-run seed** for `WatchlistStore` (no longer always-shown; removable once seeded). |
| `Settings/WatchlistStore.cs` | JSON-persisted tracked instruments, each carrying **two independent flags** `InWatchlist`/`IsFavorite` (favorites = the dock subset). Stores **full `DomainInstrument` identity** so searched non-catalog symbols re-price; an entry with both flags false is dropped. Source-gen `WatchlistItem`/`WatchlistJsonContext` → `market_watchlist.json`; seeds `InstrumentCatalog` on first run, else migrates the legacy `market_favorites.json` (old pins → watchlisted **and** favorited). Exposes its `Watchlist`/`Favorites` subsets as **observable `StateFlow`s** (each mutation calls `PublishState()` → re-publishes both); pages and the dock **subscribe** and re-render themselves. Replaces the old `FavoritesStore` + `Watchlist.cs`. |
| `Helpers/StateFlow.cs` | A tiny **Kotlin-StateFlow analog**, now a **thin wrapper over `System.Reactive`'s `BehaviorSubject<T>`** (the migration off the old hand-rolled version is **done** — AOT/trim is off, so the Rx dependency is taken warning-free; see the Rx done bullet). Used by the **state holders** (`WatchlistStore`, `MarketSettingsManager`) — observable *state with a current value*, which `IObservable` (no `Value`) and a bare `BehaviorSubject` (no read-only face) can't express alone. Public API unchanged: `StateFlow<T>` (read-only `Value` + `Subscribe` with **replay-on-subscribe** + distinct-until-changed; a `Subscribe(onNext, replayOnSubscribe:false)` overload opts out of the replay via `Skip(1)` — used by the priced pages' secondary flows, e.g. `HasAnyApiKey`) and writable `MutableStateFlow<T>` (`Update`). `SetValue` does **source-side** distinct-until-changed (an equal value isn't pushed, so re-publishing an unchanged subset doesn't wake its subscribers); `BehaviorSubject.OnNext` fans handlers out **outside** its lock (handlers re-read the store safely). The old hand-rolled **subscriber-count seam** (`OnActive`/`OnInactive` + the refcount + a custom `Subscription` wrapper) was **removed** once its only user, `PollTicker`, went pure Rx — Rx's `Publish().RefCount()` is the proper home for that, and Rx subscriptions are already idempotent on dispose. Plus `InstrumentListComparer` (symbol-sequence dedup for the store's two flows). |
| `Helpers/PollTicker.cs` | The **live-price poll ticker** — now **pure Rx** (no longer a `StateFlow<long>`). A process-wide singleton `IObservable<long>` = `Observable.Generate(...)` (self-rescheduling timer; per-step delay re-read from `MarketSettingsManager` each iteration so interval/on-off applies without reload, `0` = off idles on a 30 s re-check and the tick is filtered out) wrapped in **`Publish().RefCount()`** — the WhileSubscribed seam that starts the loop on the first subscriber and tears it down on the last (Generate never completes, so disposal is silent and resubscribe restarts cleanly). `Defer`/`Finally` log the start/stop at the refcount edges. Surfaces attach via the **guarded** `PollTicker.Subscribe(onTick)` (the raw stream is private): the handler is wrapped in try/catch (swallow + `Log.Error`→Sentry) so a throwing tick handler can't trip Rx's `SafeObserver` into tearing down the shared multicast stream (which would kill polling for every surface). Handlers still offload heavy work via `Task.Run`. |
| `Helpers/HttpRetry.cs` | **Shared 429 back-off** at the HTTP seam. `SendAsync(send, tag, ct)` takes a request **thunk** (each retry must re-issue a fresh `HttpResponseMessage`) and, on HTTP `429`, honors a short `Retry-After` else backs off `1s`→`2s` (max 3 attempts, **bails** if the wait would exceed an `8s` cap — per-minute windows don't clear in seconds, so hammering only burns quota). Returns the final response (success / non-429 error / surviving 429) so callers inspect status exactly as before — a **drop-in** at each `GetAsync`. Also the single choke point that feeds `RateLimitSignal` (2xx → off, surviving 429 → on). Used by Finnhub + Twelve Data (not keyless Frankfurter). |
| `Helpers/RateLimitSignal.cs` | Process-wide **"are we throttled" flag** — a `MutableStateFlow<bool>` behind a read-only `StateFlow<bool>` (same state-holder idiom as `WatchlistStore`/`MarketSettingsManager`). `ReportRateLimited()`/`ReportSuccess()` flip it (distinct-until-changed); priced surfaces **subscribe** and re-render. Intentionally **global, not per-symbol** (a free-tier limit is key-wide). |
| `Helpers/RateLimitHint.cs` | The **rate-limited banner row** (parallels `ApiKeyHint.MissingKeyRow()`): `Row()` returns an amber "Rate-limited — showing last known prices" `ListItem` (Enter = **`NoOpCommand`**, purely informational — it's the default-selected first row, so it must not navigate) when `RateLimitSignal` is set **and** the `ShowRateLimitErrors` setting is on, else `null`. Pinned to the **top** so it's seen without scrolling: `PricedListPage.GetItems` inserts it at index 0; `SearchPage.SearchItems` inserts it at index 1 (just under the Enter-to-search action, which must stay first). |
| `Data/Finnhub/ApiFinnhubQuoteDto.cs` | Raw `/quote` DTO + the **single** `FinnhubJsonContext` (all `[JsonSerializable]` live here — see AOT/trim gotcha). |
| `Data/Finnhub/ApiFinnhubSearchDto.cs` | Raw `/search` DTOs (`ApiFinnhubSearchDto` / `...ResultDto`); registered on `FinnhubJsonContext` in the quote file. |
| `Data/Finnhub/ApiFinnhubCandleDto.cs` | Raw `/stock/candle` + `/crypto/candle` DTO (parallel `c/h/l/o/t/v` arrays + `s` status); registered on `FinnhubJsonContext` in the quote file. **Premium** (free key → 403). |
| `Data/TwelveData/ApiTwelveDataQuoteDto.cs` | Raw `/quote` DTO + the **single** `TwelveDataJsonContext` (all `[JsonSerializable]` here, incl. the keyed `Dictionary<string,…>` batch response; `NumberHandling=AllowReadingFromString` since TD encodes numbers as JSON strings). |
| `Data/TwelveData/ApiTwelveDataCandleDto.cs` | Raw `/time_series` DTOs (`ApiTwelveDataTimeSeriesDto` + per-bar `ApiTwelveDataValueDto`, **newest-first** — provider reverses to oldest-first); registered on `TwelveDataJsonContext`. **Free tier** (unlike Finnhub candles). |
| `Data/TwelveData/ApiTwelveDataSearchDto.cs` | Raw `/symbol_search` DTOs (`data[]` of symbol/instrument_name/instrument_type); registered on `TwelveDataJsonContext`. |
| `Models/{AssetCategory,DomainInstrument,DomainQuote,UiQuote}.cs` | the quote model layers. `DomainQuote` now carries a **`Currency`** (ISO-4217 native currency of the price, default `"USD"`); `UiQuote.FormatPrice` renders stock/crypto in that native currency's symbol (FX-rate prices stay raw 4-decimal). See "Multi-currency portfolio conversion (done)". |
| `Models/ChartRange.cs` | `ChartRange` (1D/1W/1M/1Y/5Y) **+ `CandleInterval`** enums + neutral helpers (`Label`/`Lookback`/`Interval`/`FromLabel`). Provider-agnostic — **no resolution tokens here** (those live in the provider). |
| `Models/DomainCandleSeries.cs` | Domain history: `Symbol`, `Range`, ordered `CandlePoint`s (`Time`+`Close`), `IsValid`; `Invalid(...)` factory + `First/LastClose`. No formatting. |
| `Models/UiCandleSeries.cs` | Ui projection of a `DomainCandleSeries`: `IsUp`, `FormatPrice`, `FormatRangeChange` (Robinhood-style net change over the selected range), `ChartImageUrl()`. The ONLY place chart formatting/SVG live. |
| `Helpers/ChartHelper.cs` | Ports Perf Monitor's **SVG-sparkline-as-`data:`-URI** (pure `System.Xml.Linq`, AOT-safe), generalized to plot N points + normalize Y to the series min/max, recolored green/red. Draws a **faint quarter gridline box** (0/¼/½/¾/1 on both axes) so the scale reads at a glance. **No numeric tick labels:** the host rasterizes the data-URI through Direct2D's SVG engine (`ID2D1SvgDocument`), which renders lines/polylines/rects/gradients but **silently drops `<text>`** — so axis numbers aren't possible on this surface (the grid is decorative only). Grid uses a theme-neutral mid-gray (static image can't read the host theme). Only caller: `UiCandleSeries`. |
| `Helpers/AssetIconResolver.cs` | Instrument identity → row/dock `IconInfo`. Builds **Elbstream** logo URLs by category (`/logos/symbol/{t}`, `/logos/crypto/{c}`, FX pair → base currency's flag `/logos/country/{iso2}` via a currency→country map, `?format=png`); Segoe-glyph fallback for unmapped currencies/unknown. **Zero API calls** (the host fetches the URL). Also the shared `AttributionRow()` factory (the required Elbstream credit). See "Asset logos (done)". |
| `Helpers/CurrencyConverter.cs` | Process-wide singleton that converts money between currencies via Frankfurter's keyless **`/latest`** spot rates (same ECB source as the FX provider). Two-phase for the sync-render constraint: **`PrimeAsync(preferred, natives)`** fetches every not-yet-fresh `native→preferred` rate in **one batched call** (base=preferred → invert each) and caches per ordered pair with a **~1 h TTL** (negative results cached too); **`TryGetRate(from, to)`** is the synchronous cache read used while rendering (`from==to → 1`, unknown/unsupported → `null`). Used only by `PortfolioPage` (off the price-load path via `OnPriceCacheUpdated`). |
| `Helpers/CurrencyFormat.cs` | UI helper: render a `decimal` as money in an ISO-4217 currency — the home for the per-currency **symbol** (`$`/`€`/`£`/`¥`/…) + decimal-place rules that replaced the hardcoded `$`. `Format`/`FormatSigned` (sign-before-symbol for P&L), culture-invariant; unknown code → trailing code (`1,234.56 SGD`); JPY/KRW render 0-decimal. Used by `UiQuote`, `UiPosition`, `UiPortfolio`. |
| `Helpers/CurrencyHelper.cs` | Currency conventions the **providers** apply when stamping `DomainQuote.Currency`: `NormalizeStockQuote` folds London's **GBX/GBp pence → GBP** (price & change ÷100; the 100× trap) and upper-cases the code (null → `USD`); `QuoteCurrencyOfPair` returns an FX pair's quote (2nd) currency (`EURUSD → USD`). One home for the pence rule across Twelve Data + Finnhub. |
| `Pages/SymbolDetailPage.cs` | Shared per-symbol screen: nested `SymbolChartForm : FormContent` (adaptive-card chart + range tabs) + the list-management command bar. Flicker on range switch is fixed; ⚠️ the Enter-steals-focus bug is an **open known limitation** — see the chart section. |

**To add a provider** (e.g. forex): implement `IMarketDataProvider` (`Supports`, `GetQuotesAsync`,
and `SearchAsync` — return `[]` if it can't search; optionally **override `GetCandlesAsync`** to serve
chart history, else the default invalid-series opt-out applies), map its `Api*` DTO → `DomainQuote`
(and → `DomainCandleSeries` for candles), and register it in `MarketExtensionCommandsProvider`:
`new MarketRepository(new FinnhubMarketDataProvider(), new YourProvider())`. **Zero UI changes.** For
candles, the provider also translates the neutral `ChartRange.Interval`/`Lookback` into its own API
tokens — Finnhub's `ToFinnhubResolution` (`5/30/60/D/W`) is the model; `TwelveDataMarketDataProvider`
maps the same `CandleInterval` to `5min/30min/1h/1day/1week`. **Worked examples:**
`FrankfurterMarketDataProvider` (keyless FX) and `TwelveDataMarketDataProvider` (an all-three-classes
source, primary when its key is set) are both exactly this — added behind the seam with zero
UI/repository changes (only a registration line + a settings key).

**Twelve Data specifics (primary provider):**
- Base `https://api.twelvedata.com/`, key via `apikey=` query param (per-provider setting
  `TwelveDataApiKey` — see API Key). **One source for stocks + crypto + forex.** Registered FIRST so it's
  primary, but `Supports()` is **gated on the key** → a no-key install falls through to Finnhub/Frankfurter.
- Free tier **~8 credits/min, ~800/day** (1 credit/symbol) — tighter per-minute than Finnhub. So
  `GetQuotesAsync` **batches every instrument into ONE `/quote` call** (comma-joined). Response shape
  differs by count: **>1 symbol** → object keyed by symbol (`Dictionary<string, ApiTwelveDataQuoteDto>`);
  **1 symbol** → a bare quote object. A global error (bad key/429) can come back as a bare
  `{"status":"error"}` even on HTTP 200 → the parse degrades to all-invalid (kept off the Sentry path).
- Symbol formats: stock `AAPL`, crypto `BTC/USD`, FX `EUR/USD`. `ToTwelveDataSymbol` maps our neutral
  symbols (bare ticker / bare coin `BTC`→`BTC/USD` / 6-letter `EURUSD`→`EUR/USD`); `SearchAsync`
  normalizes results **back** (crypto → base coin, FX → 6-letter) so they round-trip and match the icon
  resolver + watchlist storage.
- **Candles are FREE-tier** — the headline win: `GET /time_series?symbol=&interval=&outputsize=` renders
  the symbol-detail chart on a free key (Finnhub's candles are premium → 403). `interval` via
  `ToTwelveDataInterval`; `outputsize` sized per range. Values arrive **newest-first** → the provider
  reverses to oldest-first. Numbers are JSON **strings** → `NumberHandling=AllowReadingFromString`.
- **Search** `/symbol_search?symbol=` returns identity only across all three classes; `instrument_type`
  → `AssetCategory` (equity-ish → Stock, "Digital Currency" → Crypto, "Physical Currency" → Currency).

**Finnhub specifics:**
- Base `https://finnhub.io/api/v1`, endpoint `/quote`. Free tier ~60 calls/min, ~300/day.
- **No forex on the free tier** — `OANDA:*` returns 403. FX is therefore routed to the keyless
  `FrankfurterMarketDataProvider` (ECB rates) rather than Finnhub; see Frankfurter specifics below.
- Symbol formats: stock = bare (`AAPL`), crypto = `BINANCE:{SYM}USDT`, FX = `OANDA:{BASE}_{QUOTE}`.
- An all-zero `/quote` response = invalid/unknown symbol → map to `IsValid:false`.
- **Candles (OHLCV) — PREMIUM** (free key → 403): `GET /stock/candle?symbol=&resolution=&from=&to=`
  (crypto: `/crypto/candle`, same shape). `resolution` ∈ `1,5,15,30,60,D,W,M`; `from`/`to` are **UNIX
  seconds**. Response = parallel arrays `c/h/l/o/t/v` + `s` (`ok`|`no_data`). Daily is split-adjusted;
  intraday is unadjusted and **only ~1 month per call**. `FinnhubMarketDataProvider.GetCandlesAsync`
  maps each `ChartRange`: 1D→`5`/1d, 1W→`30`/7d, 1M→`60`/31d, 1Y→`D`/365d, 5Y→`W`/5y (keeping
  1D/1W/1M inside the intraday cap). 403/429/`no_data` → invalid series → the chart shows an
  "unavailable" message rather than blanking.
- **Symbol search** `/search?q=&exchange=US` (free tier): returns identity only (`symbol`/`description`/
  `type`), **no prices**. `symbol` is the canonical id for `/quote`; `displaySymbol` is for UI. Scoped to
  **US equities** and mapped to `AssetCategory.Stock` — Finnhub's canonical `symbol` only round-trips
  through `ToFinnhubSymbol(Stock)` for plain US tickers; crypto/FX search needs symbol-format
  reconciliation (deferred). UI is **Enter-only** (the synthetic "Search Finnhub for …" item), never
  per-keystroke, to protect the rate limit. A name can list across exchanges → dedupe by symbol.

**Frankfurter specifics (FX):**
- Base `https://api.frankfurter.dev/v1/` (the old `api.frankfurter.app` 301-redirects here). **Keyless,
  no documented rate limit** → no key short-circuit, no `Has…Key` gate.
- Wraps the **ECB daily reference rates**, so data is **daily only** (one fixing per business day, ~16:00
  CET). Implications baked into the provider: a quote's **change is day-over-day** (latest vs previous
  fixing — the FX analog of Finnhub's `d`), and charts plot **daily closes** (the 1D/1W tabs show recent
  daily fixings, not intraday bars — the candle lookback is floored at 7 days so short ranges still draw).
- **One endpoint for both flows** — the time series `GET /{from}..{to}?base={BASE}&symbols={QUOTE}` →
  `{ "rates": { "2024-06-03": { "USD": 1.0842 }, … } }`. Quotes fetch a ~10-day window (≥2 fixings across
  weekends) and take the last two points; candles fetch the range's lookback. Any base works (`base=USD`
  for USDJPY is fine — Frankfurter cross-converts via EUR).
- Pairs are the neutral **6-letter `BASE+QUOTE`** (`EURUSD`); the provider splits 3+3. `SearchAsync` is a
  **local filter over `InstrumentCatalog`'s FX pairs** (Frankfurter has no symbol-search endpoint), so the
  catalog is the single source of truth for which pairs are seeded *and* searchable.
- Icons: an FX pair shows its **base currency's country flag** (Elbstream `/logos/country/{iso2}`, EUR→`eu`)
  via the `AssetIconResolver` currency→country map; unmapped currencies fall back to the bank glyph.

**AOT/trim (intentionally OFF):** this is a hobby project that does **not** trim or Native-AOT compile —
it ships **self-contained single-file JIT** (see `MarketExtension.csproj`; same shape AdbExtension
publishes to the Store), and enabling AOT/trim is a **deliberate non-goal**. The trim/AOT analyzers + the
CsWinRT AOT optimizer were **removed**, so reflection-based code (e.g. `System.Reactive`, reflection-based
`JsonSerializer`) now builds **warning-free** — there is no `IL2026`/`IL3050` enforcement anymore. (To
revive AOT readiness, restore `<IsAotCompatible>true` + the CsWinRT optimizer lines from git history.)
The existing source-gen `JsonSerializerContext`s are kept as a **convention, not a build requirement**:
`FinnhubJsonContext` (quotes + search + candles), `FrankfurterJsonContext` (FX time-series),
`TwelveDataJsonContext` (quotes + candles + search; `NumberHandling=AllowReadingFromString` for TD's
string-encoded numbers, and the keyed `Dictionary<string,…>` batch response is registered too), and
`WatchlistJsonContext` (watchlist items + legacy migration) all still work and **don't need changing** —
new JSON may use either source-gen or plain reflection.
⚠️ **Gotcha (only while a context stays source-gen):** the JSON source generator does **not** support
`[JsonSerializable]` attributes split across multiple `partial` declarations of one context — it emits a
colliding hintName (e.g. `FinnhubJsonContext.Decimal.g.cs`) and the *whole* generator silently fails,
cascading CS0534 ("does not implement … `GetTypeInfo`") onto **every** context in the build. Keep all
`[JsonSerializable]` for a given context on a **single** declaration (see `ApiFinnhubQuoteDto.cs`).

## API Key (runtime setting — no build-time key)

- **Twelve Data key (primary provider)** — `TwelveDataApiKey`/`HasTwelveDataApiKey`, same
  `market.settings.json`. When set, Twelve Data serves stocks/crypto/FX **and free charts**; its
  `Supports()` is gated on the key, so an unset key **falls back** to Finnhub/Frankfurter (no
  regression). Read per request (a key change applies without a reload); `SearchAsync` returns `[]` and
  quote/candle fetches short-circuit to "no data" when unset. Having **both** TD and Finnhub keys set is
  harmless but redundant: TD (registered first) wins all pricing/candles; Finnhub then only contributes
  extra `/search` hits (search fans out to every provider and dedupes by symbol).
- The Finnhub key is provided **exclusively at runtime** via the extension's settings
  (`Settings/MarketSettingsManager.cs`, exposed as `FinnhubApiKey`/`HasFinnhubApiKey`, persisted to
  `…/Microsoft.CmdPal/market.settings.json`). There is **no built-in/baked key** — the old
  `secrets.props` + csproj `GenerateSecrets` + `Secrets.FinnhubApiKey` machinery was removed.
- `FinnhubMarketDataProvider` reads `MarketSettingsManager.Instance.FinnhubApiKey` on each request
  (so a key change applies without a reload) and **short-circuits to "no data"** (invalid quotes /
  empty search / invalid candle series, with a `Log.Warn`) when no key is set, rather than firing
  keyless requests that would just 401.
- **Per-provider naming on purpose:** keys are named per provider (`FinnhubApiKey`, not a generic
  `ApiKey`) because future providers (e.g. a forex source) each get their own key setting here.

## Logging

- `Log.Info/Warn/Error(tag, message)` (`Helpers/Log.cs`) — tagged by component (`Finnhub`,
  `Repository`, `ComServer`, `Startup`).
- `Info`/`Warn` are `[Conditional("DEBUG")]` → **compiled out of Release** (the MSIX ships silent).
  `Error` also reports to **Sentry**, which survives into Release (no-op until a DSN is set in `Program.cs`).
- Watch live (Debug builds): VS **Debug → Attach to Process → `MarketExtension.exe`** (Managed) →
  Output window; or Sysinternals **DebugView** ("Capture Global Win32"). **NEVER log the API token.**

## Current Status / Next Steps

- **Done (this round): multi-currency portfolio conversion** — the headline portfolio wishlist item. The
  app no longer assumes USD: `DomainQuote` carries an ISO-4217 **`Currency`** (providers stamp it — Twelve
  Data from the batched `/quote` field, Finnhub via a cached `/stock/profile2` lookup, Frankfurter from the
  pair's quote currency; crypto→USD, **GBX/pence→GBP ÷100**), and the **Portfolio screen** values each
  holding in its native currency **and** converts into the user's `PortfolioCurrency` via the keyless
  Frankfurter `/latest` rates (new `Helpers/CurrencyConverter.cs`, primed off the price-load path through a
  new `PricedListPage.OnPriceCacheUpdated` hook; synchronous `TryGetRate` at render time). Totals roll up in
  the preferred currency with proper symbols (`Helpers/CurrencyFormat.cs` replaces the hardcoded `$`);
  unconvertible holdings are excluded and surfaced. `UiQuote.FormatPrice` is currency-aware app-wide too
  (a London stock shows `£2.50`). New files: `Helpers/CurrencyConverter.cs`, `CurrencyFormat.cs`,
  `CurrencyHelper.cs`, `ApiFinnhubProfileDto.cs`, `ApiFrankfurterLatestDto`. Build clean (0 warnings).
  ✅ **Live-verified**: 10 sh SPY (USD) with GBP preferred → ~£5,571 (USD value × ~0.76, a real conversion).
  See "Multi-currency portfolio conversion (done)".

- **Done (this round): rate-limit (429) back-off + a "rate-limited" banner.** Two pieces behind the
  existing provider seam — **no model-layer change**:
  - **Back-off** — a shared `Helpers/HttpRetry.cs` wraps every `Http.GetAsync` in the two **keyed** HTTP
    providers (Finnhub + Twelve Data; Frankfurter is keyless / no documented limit, so it's left out). On
    an HTTP `429` it honors a short `Retry-After`, else backs off `1s`→`2s` (max 3 attempts), bailing
    immediately if the wait would exceed an `8s` cap — because the free tiers are **per-minute** windows, a
    few-second retry rarely clears an exhausted minute, so hammering would just burn more quota. Plain
    `async`/`Task.Delay`, **not** Rx — wrapping these Task-based calls in observables just to use
    `RetryWhen` would add ceremony for no gain (the CLAUDE.md Rx framing oversold this one).
  - **Banner** — a process-wide `Helpers/RateLimitSignal.cs` (`StateFlow<bool>`, the same state-holder
    idiom as `WatchlistStore`): `HttpRetry` flips it **on** when a 429 survives back-off and **off** on any
    2xx, at that single choke point. The Twelve Data provider additionally reports it when TD encodes a
    `429` in a **200-OK body** (`{"code":429,…}`), which an HTTP-status check alone misses. The priced
    pages + Search **subscribe** (replay:false) and render `Helpers/RateLimitHint.cs` — an amber
    "Rate-limited — showing last known prices" row (Enter is a **no-op** — `NoOpCommand`, purely
    informational; it's the default-selected first row so it must not steal Enter), pinned to the **top**
    (priced pages index 0; Search index 1, just under the Enter-to-search action) so it's seen without
    scrolling — so the user knows *why* prices look stale instead of just seeing the keep-last-good values.
    A new **`Show rate-limit warnings`** toggle setting (`ShowRateLimitErrors`, default on) hides the banner
    entirely for users who'd rather not see it. It's deliberately
    **global, not per-symbol** (a free-tier limit is key-wide). The **dock band contributes to the signal**
    (its fetches go through the same providers) but doesn't render the banner — too cramped, and a
    pseudo-button warning would look out of place. Build clean (0 warnings). ⚠️ Verified to **compile**;
    a live smoke test (exhaust a free key, confirm the banner appears then clears on recovery) is worth doing.
- **Done (this round): Twelve Data provider — now the primary source.** Added
  `Data/TwelveData/TwelveDataMarketDataProvider.cs` behind the existing `IMarketDataProvider` seam with
  **zero UI/repository changes** — one API covering **stocks + crypto + forex**, registered FIRST and
  **gated on its key** so it's primary when set and **falls back** to Finnhub/Frankfurter when not. The
  headline win: **`/time_series` candles are free-tier**, so the symbol-detail chart now renders real
  history on a **free** key (Finnhub's candles are premium → 403). `GetQuotesAsync` **batches** all
  symbols into one `/quote` call (tight ~8/min free limit); `GetCandlesAsync` reverses TD's newest-first
  values; `SearchAsync` via `/symbol_search`, normalizing results back to neutral symbols. New
  `TwelveDataApiKey` setting + source-gen `TwelveDataJsonContext` (`AllowReadingFromString`). Build is
  clean (AOT/trim). ✅ Verified live end-to-end against a real Twelve Data key (quotes, batched `/quote`,
  and free-tier `/time_series` charts). See "Twelve Data specifics".
- **Done:** layered data architecture; live Finnhub provider; `MarketRepository` coordinator;
  **runtime API key + refresh-interval settings** (`MarketSettingsManager`; key is settings-only,
  no baked-in key); tagged logging; **Enter-only Finnhub `/search`**; **persistent
  watchlist + favorites** as two independent flags (`WatchlistStore`); the **three-screen UX**
  (Markets Search / Watchlist / Favorites — see below); and the **observable (StateFlow) data layer** —
  the store exposes `Watchlist`/`Favorites` as `StateFlow`s, all surfaces subscribe and re-render
  themselves, the membership commands no longer thread a manual refresh callback, and **dock refresh on
  favorites change is now live** (push, not poll). Priced pages **reconcile locally** on membership
  change (drop removed rows free, fetch only new symbols). See `Helpers/StateFlow.cs`.
- **Done (this round):** the **symbol-detail price chart with 1D / 1W / 1M / 1Y / 5Y range tabs** — real
  Finnhub candle history behind a provider-agnostic seam (`GetCandlesAsync`, `ChartRange` + `CandleInterval`,
  `DomainCandleSeries` / `UiCandleSeries`, ported `Helpers/ChartHelper.cs`, `ApiFinnhubCandleDto`). The
  Finnhub candle endpoint is **premium-gated**, so on the Finnhub path real charts need a paid key (free
  key → "requires paid plan"; the mock provider draws synthetic candles for offline preview). **Update:
  the Twelve Data provider (now primary) serves candles on the FREE tier**, so a free Twelve Data key
  renders real charts — the premium gate now applies only to the Finnhub fallback path. The
  **range-switch flicker is fixed**; the
  **Enter-steals-focus bug is UNSOLVED** and left as a documented known limitation (two fixes tried and
  abandoned — see the "Symbol detail + live chart" section).
- **Done (this round):** **live price polling** — prices now auto-refresh on a timer while a priced
  surface is visible (Markets Watchlist / Favorites + the Favorites dock band). Realized via
  `Helpers/PollTicker.cs` (a singleton ticker — originally a `StateFlow<long>` on the `OnActive`/`OnInactive`
  subscriber-count seam, **now pure Rx** `Observable.Generate(...).Publish().RefCount()`), and per-surface
  **silent** `PollRefresh()` methods that re-price in place with **no spinner/flicker** (cache not
  cleared, prices swap when the fetch lands). Honors the existing interval setting (default 5 min,
  `0` = off, applied without reload). A **keep-last-good guard** in `PricedListPage.LoadQuotes(silent)`
  stops a transient bad poll (e.g. a 429 mapped to an invalid quote) from blanking a price that was
  fine. The chart now live-refreshes its visible range on the same ticker (see below). See "Live price polling (done)".
- **Done (this round): FX provider (keyless Frankfurter)** — forex is now live behind the existing
  `IMarketDataProvider` seam with **zero UI/repository changes**. `Data/Frankfurter/FrankfurterMarketDataProvider.cs`
  (`Supports(Currency)`) wraps the ECB daily reference rates via Frankfurter (`api.frankfurter.dev`, **no
  key**): it splits a 6-letter pair, uses one time-series endpoint for both quotes (latest vs previous
  fixing → **day-over-day** change) and candles (daily closes). `InstrumentCatalog` re-adds `EURUSD` /
  `GBPUSD` / `USDJPY`; the provider is registered alongside Finnhub in `MarketExtensionCommandsProvider`;
  `AssetIconResolver` now shows the base currency's **country flag** (EUR→`eu`). FX "search" is a local
  filter over the catalog's pairs (Frankfurter has no symbol endpoint). Verified end-to-end against the
  live API. See "Frankfurter specifics (FX)". (Caveats: ECB is **daily-only** — no intraday FX bars; FX
  pairs reach existing installs via the catalog seed / local search, since Finnhub `/search` is US-equity.)
- **Done (this round): symbol-detail chart live refresh** — the open chart now re-fetches its **visible
  range** on each `PollTicker` tick (not just one fetch per tab tap), sharing the same ticker +
  `Publish().RefCount()` lifecycle as the priced list pages and dock. `SymbolDetailPage` subscribes the chart in its
  visible-lifecycle hook (`replayOnSubscribe:false`, so opening the page doesn't double-fetch — `Start()`
  already did the first load) and disposes it on hide with the rest. `SymbolChartForm.PollRefresh()`
  re-prices in place: **silent** (no "Loading…" write, so no flicker), **bypasses the per-range cache** so
  the on-screen range actually refreshes (then refreshes that cache entry), **reuses the generation guard**
  so a concurrent tab tap still wins, and **keeps the last good chart** if a poll returns empty (the chart's
  analog of `PricedListPage`'s keep-last-good). Caveat: on the **Finnhub fallback** path candles are
  premium-gated, so a free Finnhub key just re-fetches a 403 each tick; with **Twelve Data** primary its
  free-tier candles refresh for real.
- **Done (this round): the Portfolio screen.** **Markets Portfolio** now tracks real holdings (a quantity
  per symbol), priced live, with a totals summary on top and **daily P&L** per holding — behind the existing
  data seam with **no provider/repository changes**. New: `Settings/PortfolioStore.cs` (modeled on
  `WatchlistStore`, `market_portfolio.json`, two flows `Positions`/`Instruments` — the latter
  **reference-equality so quantity-only edits re-emit**), `Models/DomainPosition`/`UiPosition`/`UiPortfolio`,
  `Pages/SetQuantityPage.cs` (an adaptive-card `Input.Number` editor), and `Commands/PortfolioCommands.cs`.
  `PortfolioPage` is a **`PricedListPage` subclass** that reuses all the caching/polling plumbing; a new
  `PricedListPage.LeadingRows(...)` hook (default empty, others unaffected) renders the totals row from the
  full priced set. Per the user's choices this pass: **daily P&L only** (cost basis stored but no UI),
  **no dock band yet**, and **all add/edit/remove on the symbol detail page** (rows just open it, like every
  other list). Build clean (0 warnings). ⚠️ Verified to **compile**; a live smoke test (add a holding, see
  the total roll up, edit/remove, confirm persistence) is worth doing. See "Portfolio screen (DONE…)".
- **Deferred:** richer **per-symbol** error UX — a typed `QuoteStatus` (Ok/Invalid/RateLimited/NoKey) on
  `DomainQuote` so each row could render its own state. Deliberately **not** built: the rate-limit signal
  shipped this round is global (one throttle flag for the whole key) because a free-tier limit is key-wide,
  not per-symbol, and a typed status would ripple through all four providers + `UiQuote` + the keep-last-good
  guard for marginal gain. **(Done this round:** 429 **back-off + a "rate-limited" banner** — see the
  dedicated bullet above; the keep-last-good guard is no longer the whole story.)
  **(Resolved by the Twelve Data provider:** crypto + FX symbol search now works across all three classes
  — TD's `/symbol_search` maps `instrument_type` → category and normalizes symbols back to neutral form,
  and the repository fans search out to every provider. The original gap was Finnhub-only; it now persists
  **only on the keyless fallback path** — without a TD key, Finnhub search is US-equities-only, Frankfurter
  covers FX via a local catalog filter, and crypto search is unavailable.)
- **Done (this round): stale-revisit catch-up** — closes the "short visit" gap in live polling. The
  priced pages are long-lived singletons whose `_priceCache` survives navigation, so revisiting an
  unchanged set repainted cached prices with **no fetch**, while every revisit restarted the poll timer
  from a full interval — so visits shorter than the interval never saw a refreshed price (a pinned dock
  masked it). Now `PricedListPage` stamps a freshness clock (`_lastFullPriceTicks`, set on every
  whole-set re-price; a partial add doesn't advance it) and, when a fully-cached set becomes visible,
  fires **one silent catch-up re-price if the prices have aged ≥ one interval** (`RefreshStaleQuotes`).
  Bounded to one fetch per revisit, only when stale, gated on `AutoRefreshEnabled` — liveness without
  burning the Finnhub budget. This supersedes the old "poll-timer debounce" deferred item.
- **Done (this round): asset logos as row/dock icons** — every instrument surface (Markets Search results,
  Watchlist, Favorites, the Favorites dock band, and the `SymbolDetailPage` header) now shows the
  instrument's **real logo** instead of the generic favicon. Logos come from **Elbstream's keyless CDN**
  addressed directly by symbol (`Helpers/AssetIconResolver.cs`) — **zero API calls / no cache / no DTO**
  (the host fetches the URL). Elbstream is free **with attribution**, so a "Logos provided by Elbstream"
  credit (clickable via the new `Commands/OpenUrlCommand.cs` + `ProcessHelper.OpenUrl`) appears on every
  logo-bearing page; the dock band is an accepted gray area. Per-category Segoe glyph fallback for
  Currency/unknown. See "Asset logos (done)".
- **Wishlist:** ~~**(1) multi-currency portfolio conversion**~~ — **DONE this round** (see "Done (this
  round): multi-currency portfolio conversion" + "Multi-currency portfolio conversion (done)"). Remaining:
  **(2)** the **Portfolio dock band** (a one-line total-value + daily-P&L strip next to the favorites band —
  the Portfolio *screen* itself is now done); and **(3)** **cost-basis / total-return** reporting (the
  `CostBasis` field is already stored, just not surfaced). Both designed below.
- **Done (this round): migrated the observable layer to Rx.NET (`System.Reactive`).** Two parts:
  - **`StateFlow` → thin wrapper over `BehaviorSubject<T>`** (the blocker was gone once AOT/trim went off —
    the trim/AOT analyzers were removed, so `System.Reactive` 6.0.1 is taken **warning-free**, CA1001
    suppressed on the process-lifetime singleton). The **public API is preserved** (`Value`, both `Subscribe`
    overloads, `Update`, `InstrumentListComparer`), so the **state-holder consumers (`WatchlistStore`,
    `MarketSettingsManager`) and every page/dock subscriber did not change** —
    verified by a clean build (0 warnings). Mechanics preserved 1:1: `BehaviorSubject` gives value +
    replay-on-subscribe + thread-safe fan-out; `replayOnSubscribe:false` → `Skip(1)`; `SetValue` keeps
    **source-side** distinct-until-changed (equal value not pushed → unchanged subset doesn't wake
    subscribers); handlers fan out **outside** the subject's lock (so the store re-read is deadlock-free).
  - **`PollTicker` → pure Rx** (`Observable.Generate(...).Publish().RefCount()`), since it was the one
    consumer that was a *poor* `StateFlow` fit (an event stream abusing `StateFlow<long>` + a counter, only
    to borrow the refcount seam). It no longer inherits `StateFlow`; `RefCount()` provides the
    WhileSubscribed lifecycle, deleting the `CancellationTokenSource`, the lock, the `OnActive`/`OnInactive`
    overrides, and the CA1001 suppression. The 3 subscribers dropped `, replayOnSubscribe: false` (a pure
    event stream has no replay to suppress). Net effect: with no consumer left, `StateFlow`'s entire
    subscriber-count seam (`OnActive`/`OnInactive` + the hand-rolled refcount + custom `Subscription`
    wrapper) was **deleted** — `StateFlow` is now a genuinely thin `BehaviorSubject` wrapper.
  - **Wiring:** `System.Reactive` added to `Directory.Packages.props` (CPM) + a `PackageReference` in the
    csproj. **This was the swap only** — the payoff (Rx's **operator library**) lands with the deferred work
    it unlocks: 429 **back-off** (`Retry`/`RetryWhen` + exponential delay), **debounced** search-as-you-type
    (`Throttle` — would let `SearchPage` drop its Enter-only rate-limit guard), and multi-provider **merge**
    in `MarketRepository` (`CombineLatest`/`Merge`). **Caveat:** Rx schedulers are mostly moot here (the
    CmdPal host already marshals `RaiseItemsChanged`), so this bought **operators, not threading**. ⚠️
    Verified to **compile** clean; a live smoke test of polling (open a priced page, watch prices refresh on
    the interval; hide/reshow to confirm the loop restarts) is worth doing.

### Three-screen UX (done)

Three top-level commands in `MarketExtensionCommandsProvider.TopLevelCommands()`, backed by **two
independent membership flags** per instrument in `WatchlistStore` — an instrument can be on either
list, both, or neither (so "watchlist" and "favorites" are genuinely separate sets, not subset/superset).
All three command titles share the **`Markets ` prefix** so they group together (and don't pollute the
namespace) when searching the Command Palette root:

**Enter on any row opens the shared `Pages/SymbolDetailPage.cs`** — the per-symbol detail screen
(`ContentPage`; **now renders a live price chart with range tabs** — see the chart section), which is
**the single place for list management**. Its
command bar carries the add/remove-watchlist (**Enter**) and add/remove-favorite (**Ctrl+Enter**)
actions, labelled for the instrument's current state, and the page **subscribes to the two
`WatchlistStore` flows** so toggling there (the commands `KeepOpen`) flips the buttons + body in place.
List rows therefore carry **no context actions at all** — they only navigate into the detail page. The
screens:

1. **Markets Search** (`Pages/SearchPage.cs`, default entry) — the Enter-only `/search` flow. On a
   result: **Enter** → open detail page (where it's added to the watchlist/favorites). The subtitle
   still reflects current membership. Empty box links to the other two screens.
2. **Markets Watchlist** (`Pages/WatchlistPage.cs`) — the tracked instruments, priced and grouped by
   class; a ★ marks favorites. **Enter** → open detail page.
3. **Markets Favorites** (`Pages/FavoritesPage.cs`) — the curated subset; **only favorites render in
   the dock band** (`FavoritesDockPage` subscribes to `WatchlistStore.Favorites`). **Enter** → open
   detail page. Each **dock button also opens the detail page** when clicked. A pinned band now
   updates **immediately** when favorites change anywhere (it subscribes to the flow while visible), not
   just on reopen.

Watchlist + Favorites share `Pages/PricedListPage.cs`, which **subscribes to its `StateFlow`** in the
`INotifyItemsChanged` add/remove lifecycle (StateFlow's replay-on-subscribe drives the first price load;
later membership pushes trigger a **local reconcile** — drop departed rows with no network call, fetch
only newly-added symbols) + local filter. A page can also observe secondary flows for re-render only via
`RelistTriggers` (Watchlist uses it to keep its ★ current when favorites change). The catalog seeds the
watchlist **once** on first run; thereafter every row is user-removable. The membership actions live in
`Commands/MembershipCommands.cs`
(`AddToWatchlist`/`RemoveFromWatchlist`/`AddToFavorites`/`RemoveFromFavorites`/`ToggleFavorite`) — each
just **mutates the store** (no manual UI callback: the flows re-render every live surface) and
**shows a confirmation toast** (e.g. "Added AAPL to watchlist") while **keeping the palette open** so
the user gets explicit feedback and can keep editing.

### Live price polling (done)

Priced surfaces used to fetch **once** when they became visible (the StateFlow replay-on-subscribe that
drives the first load). They now also auto-refresh on a timer while visible: **default 5 min, settings-
configurable** (0 = off). Built on the ticker's subscriber-count lifecycle (now Rx `Publish().RefCount()`).

- **Where (in scope):** `Pages/PricedListPage.cs` (Markets Watchlist + Markets Favorites),
  `Pages/FavoritesDockPage.cs`, and **the `SymbolDetailPage` chart** (its visible range re-fetches on each
  tick via `SymbolChartForm.PollRefresh()` — see the chart section). `SearchPage` is exempt (identity-only
  results, no prices).
- **The ticker = `Helpers/PollTicker.cs`** — **pure Rx** (originally `PollTicker : StateFlow<long>`; the Rx
  migration converted it). A process-wide singleton `IObservable<long>` = `Observable.Generate(...)`
  (a self-rescheduling timer that emits a tick after each per-step delay) wrapped in **`Publish().RefCount()`**.
  `RefCount()` IS the OnActive/OnInactive seam: the Generate loop **starts on the first subscriber** (0→1)
  and is **torn down on the last unsubscribe** (1→0), so polling runs only while something is watching.
  Generate's `condition` is always true so the source **never completes** — teardown is therefore a silent
  disposal (no OnCompleted/OnError reaches the shared subject), and a later resubscribe restarts cleanly
  (so a rapid inactive→active bounce just reconnects; no two-loops hazard, no manual CTS/lock). The
  `timeSelector` re-reads `MarketSettingsManager` each iteration (interval/on-off applies without a reload);
  when off it idles on a 30 s re-check and a `.Where` drops the tick, so toggling it back on resumes. (The
  old design used `StateFlow<long>` + a tick counter because re-emitting the membership list would be
  swallowed by distinct-until-changed; the Rx version sidesteps that entirely — a tick is a proper event,
  not deduped state.)
- **No double-fetch on open.** The ticker is a **pure event stream** (not a `BehaviorSubject`), so there is
  no replay to suppress: surfaces just `Subscribe(_ => PollRefresh())` and opening a page doesn't fire an
  extra fetch (the membership flow's replay already paints the initial prices); the ticker only drives
  *subsequent* refreshes.
- **Silent, flicker-free refresh.** Each surface's `PollRefresh()` re-prices **without** clearing its
  cache or setting `IsLoading`, so the on-screen prices stay put and are swapped in place once the new
  quotes land (contrast the manual "Refresh 🔄" row, which still clears + spins). `PricedListPage`
  reuses its existing `LoadQuotes` + `_fetchGeneration` guard (so a membership change still wins);
  `FavoritesDockPage` just re-runs `LoadQuotes` (which assigns `_quotes` without a pre-clear).
- **Keep-last-good guard.** `PricedListPage.LoadQuotes(…, silent: true)` won't overwrite a **valid**
  cached `UiQuote` with an **invalid** poll result, so a transient bad poll (e.g. a 429 mapped to an
  invalid quote) degrades gracefully instead of blanking a price that was fine. This is a first step, not
  the full 429 story (back-off + an explicit "rate-limited" state remain deferred).
- **Shared-source refcount comes free.** `FavoritesPage` (via `PricedListPage`) and `FavoritesDockPage`
  both subscribe to the **one** `PollTicker.Instance`: its refcount keeps the timer alive while *any*
  priced surface is visible and stops it only when *all* are gone — a pinned dock keeps it warm.
- **Stale-revisit catch-up (the timer-reset fix).** Because the priced pages are long-lived singletons
  whose `_priceCache` survives navigation, revisiting an unchanged set repaints cached prices with **no
  fetch**, and each revisit restarts the `PollTicker` countdown from a full interval (it resets on the
  0→1 subscriber transition) — so visits shorter than the interval never refreshed (a pinned dock masked
  it, since it holds the subscriber count above 0). `PricedListPage` now stamps `_lastFullPriceTicks` on
  every whole-set re-price (a partial add of new symbols does **not** advance it, so the older rows keep
  aging) and, when a fully-cached set becomes visible, `RefreshStaleQuotes()` fires **one silent catch-up
  re-price if those prices have aged ≥ one `RefreshInterval`**. One fetch per revisit, only when stale,
  gated on `AutoRefreshEnabled` (so 0 = off stays off) — maximal liveness without extra API calls. The
  `FavoritesDockPage` doesn't need this: it refetches on every show (its favorites-flow replay calls
  `RefreshQuotes`, which clears `_quotes`).
- **Settings.** `Settings/MarketSettingsManager.cs` (`JsonSettingsManager` singleton,
  `…/market.settings.json`, wired via `Settings = MarketSettingsManager.Instance.Settings;`). The refresh
  interval is a numeric `TextSetting` **in minutes, default 5** (0 = off), surfaced as `RefreshMinutes` /
  `RefreshInterval` (TimeSpan) / `AutoRefreshEnabled`; the ticker reads these each tick. A
  **`ToggleSetting` `Show rate-limit warnings`** (default on, surfaced as `ShowRateLimitErrors`) gates the
  rate-limited banner — read pull-style by `RateLimitHint.Row()`, so toggling it applies on the next
  re-render. (Same manager also holds the Finnhub API key — see the "API Key" section.)
- ⚠️ **Rate-limit tension (still real):** Finnhub free tier is **~60 calls/min AND ~300/day**, and
  `GetQuotesAsync` issues **one `/quote` call per instrument**. Polling N instruments at interval T burns
  the budget fast at a low T — e.g. 6 favorites every 60 s = 360 calls/hour, **exhausting ~300/day in
  under an hour**. The **5-min default is gentler** (6 favorites = ~72 calls/hour). In place today: the
  configurable interval + **Off** (0) escape, polling **only while visible**, the keep-last-good guard,
  and now **429 back-off + a "rate-limited" banner** (`HttpRetry` / `RateLimitSignal` / `RateLimitHint` —
  see the "Done (this round)" bullet). Note the back-off intentionally **bails fast** rather than retrying
  hard, since these are per-minute windows; a batched-quote provider (Twelve Data already does this) is the
  real relief. ⚠️ Note: each visible surface polls independently, so a pinned favorites
  dock **and** an open favorites page both fetch favorites each tick (no shared-fetch dedup). This is an
  **accepted trade-off, not a todo** — at most three priced surfaces exist (Watchlist / Favorites page /
  Favorites dock) and the only real overlap is dock + Favorites page on one small set, so the worst case
  is ~2–3× on a handful of symbols. A repository-level in-flight coalescing cache would close it cheaply
  if it ever matters, but it's deliberately **not** planned.

### Dock refresh on favorites change (done)

A pinned `FavoritesDockPage` updates the instant a favorite changes anywhere, instead of going stale
until reopened. Implemented via the observable layer:

- `WatchlistStore.Favorites` is a `StateFlow`; every favorite flip re-publishes it (`PublishState()`),
  deduped by `InstrumentListComparer` so a watchlist-only change doesn't wake favorites subscribers.
- `FavoritesDockPage` **subscribes in its `add` accessor and disposes the `IDisposable` in `remove`**
  (the Loaded/Unloaded lifecycle), calling `RefreshQuotes()` on each emission. Subscribing only while
  visible keeps a hidden band from doing work and avoids leaks.
- Complementary to the (still-pending) polling: polling catches *price* drift on a timer; this pushes
  *membership* changes immediately, and still works when polling is **Off**.

### Portfolio screen (DONE — screen built; dock band still deferred)

**Markets Portfolio** is live: a fourth screen off the Markets hub tracking actual *holdings* (a quantity
per symbol), priced live, with a **totals summary** pinned on top and **daily P&L** per holding. Reuses
the existing data seam with **no provider/repository changes** — `MarketRepository.GetQuotesAsync` already
returns per-instrument daily change.

**Scope shipped:** quantity + **daily P&L only**. `DomainPosition`/`PortfolioItem` carry an optional
`CostBasis` (persisted, **no UI yet**), so unrealized/total return is a cheap follow-up.

**Data model** (the usual `Api`/`Domain`/`Ui` layering; formatting only in `Ui*`):
- `Settings/PortfolioStore.cs` — JSON-persisted holdings, modeled on `WatchlistStore` (singleton, lock,
  source-gen `PortfolioJsonContext` → `market_portfolio.json`). Each entry = symbol + name + category +
  **quantity** (`decimal`, so crypto fractions work) + optional cost basis. **No first-run seed** (empty
  by default). Exposes two `StateFlow`s: `Positions` (instrument + quantity — for rendering, the totals,
  and the detail-page command bar) and `Instruments` (symbols to price — for the `PricedListPage`). Both
  use **default reference equality, NOT `InstrumentListComparer`**, so EVERY mutation re-emits — including
  a quantity-only edit that leaves the symbol set unchanged. That's what makes an edited quantity repaint:
  the base reconcile finds nothing missing → no fetch → the re-render just reads the new quantity.
- `Models/DomainPosition.cs` — `DomainInstrument` + quantity + optional cost basis, **no prices**.
- `Models/UiPosition.cs` + `UiPortfolio.cs` — presentation: `UiPosition` combines a `DomainQuote` with the
  quantity → `MarketValue` (qty × price), `DailyPnL` (qty × `DomainQuote.Change`), `Format*()` helpers (the
  only place position formatting lives, like `UiQuote`); `UiPortfolio.From(...)` rolls up the totals.

**Daily P&L:** per position = `Quantity × DomainQuote.Change`. Total value = Σ market value; total daily
P&L = Σ daily P&L; aggregate **percent = `totalDailyPnL / (totalValue − totalDailyPnL)`** (vs. yesterday's
close) — not an average of the per-position percents (guards ÷0). `IsValid:false` holdings are excluded
from the totals (shown as unpriced rows).

**Screen** (`Pages/PortfolioPage.cs`): a **`PricedListPage` subclass** (like Watchlist/Favorites), so it
inherits all the caching/polling/reconcile/keep-last-good plumbing — it just observes
`PortfolioStore.Instruments`. Per-row quantity is read from the store in `BuildRow` (the way `WatchlistPage`
reads `IsFavorite`). The **totals summary** is the first row, rendered via a new
`PricedListPage.LeadingRows(pricedQuotes)` **hook** (default empty → Watchlist/Favorites unaffected), fed
the **full unfiltered** priced set so the total reflects the whole portfolio even while search filters the
rows. Rows render "AAPL · 10 sh" → market value + daily P&L.

**Management lives on the symbol detail page** (consistent with every other list — Portfolio rows just open
`SymbolDetailPage` on Enter, **no context actions**). `SymbolDetailPage.BuildCommands` gained portfolio
actions in the overflow: **Add to Portfolio** / **Edit holding** (both open `Pages/SetQuantityPage.cs`,
which auto-labels itself by current membership) plus **Remove from Portfolio**
(`Commands/PortfolioCommands.cs`) once held. The page also subscribes to `PortfolioStore.Positions`, so the
bar flips Add→Edit/Remove the instant a quantity is saved.

**`Pages/SetQuantityPage.cs`** — the quantity editor: a `ContentPage` whose single `FormContent` is an
adaptive card with one `Input.Number` (prefilled with the current holding, 0 when adding) + a Save action.
`SubmitForm` parses + validates (> 0), calls `PortfolioStore.SetPosition`, toasts, and `GoBack()`s to the
detail page. The single-`FormContent` auto-focus quirk (that plagues the chart) is **harmless/helpful
here** — it drops the cursor straight into the number field.

**Still deferred — the dock band** (`Pages/PortfolioDockPage.cs`): a second band in `GetDockBands()` next
to favorites, one line of total value + daily P&L. **Not built this pass.** Would need its own non-empty
`Command.Id` (`com.costafotiadis.market.dock.portfolio` — see `reference/dock-support.md`) and would inherit
the favorites band's live-refresh story (the polling + push-on-change work above).

### Multi-currency portfolio conversion (done)

**The `$`-everywhere assumption is gone.** Holdings priced in any currency are now valued in **both** their
native currency **and** the user's `PortfolioCurrency`, and the portfolio total is rolled up in the preferred
currency. Built behind the existing data seam with **no repository changes** — only `DomainQuote` gained a
field and `PortfolioPage` gained an FX-priming step.

**1 — Capture native currency (static metadata, resolved once per provider seam).** `DomainQuote` carries an
ISO-4217 **`Currency`** (default `"USD"`, so every existing construction stays correct). Providers stamp it
when mapping their `Api*` DTO; the **`Helpers/CurrencyHelper.cs`** rules are shared so each provider does it
identically:
- **Twelve Data (primary):** added `currency` to `ApiTwelveDataQuoteDto` — it rides in the batched `/quote`
  we already fetch (**zero extra calls**). Mapped per category: stock → the field (normalized), crypto → USD,
  FX → the pair's quote currency.
- **Finnhub (fallback):** `/quote` has no currency, so stocks resolve via **`/stock/profile2?symbol=`**,
  cached **per-symbol forever** (`StockCurrencyCache`) — one extra call per *new* stock symbol per session
  (US tickers just confirm USD); crypto short-circuits to USD with no call; FX isn't served here. The USD
  fallback on a failed profile2 is **cached too**, so a flaky profile2 can't double Finnhub's poll volume.
- **Frankfurter (FX):** stamps the pair's **quote currency** (`EURUSD → USD`, `USDJPY → JPY`).
- ⚠️ **GBX/pence trap handled:** `CurrencyHelper.NormalizeStockQuote` folds London's `GBp`/`GBX` (pence) to
  **GBP**, dividing price *and* change by 100, so UK holdings aren't 100× too large. The Domain layer only
  ever sees major-unit prices in a major-unit code.

**2 — Convert via `Helpers/CurrencyConverter.cs`** (keyless Frankfurter `/latest`, the same ECB source as the
FX provider). Because the screen renders synchronously but FX is a fetch, it's two-phase: `PrimeAsync` fetches
every not-yet-fresh `native→preferred` rate in **one batched call** (cached per pair, ~1 h TTL, negatives
cached too); `TryGetRate` is the synchronous cache read used while building rows (`from==to → 1`, unknown →
`null`). An all-USD portfolio with USD preferred does **zero** network (rate 1 short-circuit).

**3 — Display (`UiPosition`/`UiPortfolio`, the only place the formatting lives).** `UiPosition` takes the
preferred currency + the `native→preferred` rate and exposes native value/P&L **and** their converted forms;
a row shows e.g. `£75.00 (≈$98.70)` and its P&L in the converted currency (so per-row P&L sums to the total).
`UiPortfolio.From(positions, preferred)` sums the **converted** values and formats the total via
`CurrencyFormat` with the preferred currency's symbol. `Helpers/CurrencyFormat.cs` owns the per-currency
symbol/decimals (replacing the hardcoded `$#,##0.00`).

**Wiring (`PortfolioPage` + one base hook).** Rates are primed off the price-load path: `PricedListPage` gained
a `protected virtual void OnPriceCacheUpdated()` hook (fired whenever the price cache changes; default no-op,
so Watchlist/Favorites are unaffected) + a `SnapshotPricedQuotes()` accessor. `PortfolioPage` overrides the
hook to `PrimeAsync` the current currencies then re-render, and reads `TryGetRate` synchronously in
`LeadingRows`/`BuildRow`. First paint shows native-only, then swaps in converted values when the rates land
(progressive, no flicker). Beyond the portfolio, `UiQuote.FormatPrice` is now currency-aware too, so a London
stock shows `£2.50` on the Watchlist/dock instead of `$2.50`.

**Excluded from the total** (and surfaced as "*N* not converted" on the summary): a holding whose native
currency the ECB set can't convert. `IsValid:false` (unpriced) holdings remain excluded as before.

**Caveats / not done.** Reporting currency is cached **in-memory per session** (Finnhub `StockCurrencyCache`),
not persisted with the instrument — a reload re-resolves it (one profile2 call per stock). Changing the
`PortfolioCurrency` setting applies on the **next price refresh/revisit** (pull-style, like the other
settings), not instantly. ✅ **Live-verified** end-to-end: a USD holding (10 sh SPY) with **GBP** preferred
rolled up to ~£5,571 — i.e. the USD value × ~0.76, a genuine conversion, not a relabel. Still worth a spot
check: the **GBX/pence** path (a London `.L` stock → ÷100 → GBP) and a **non-USD native** holding priced by
Finnhub via profile2. Build clean (0 warnings).

### Symbol detail + live chart — for ANY instrument (CHART DONE; flicker fixed, Enter/focus bug open)

**App-wide, not portfolio-specific.** The shared `Pages/SymbolDetailPage.cs` opens from any row (Enter)
and every dock-button click, and **now renders a real price chart with Robinhood-style range tabs
(1D / 1W / 1M / 1Y / 5Y)**.

**What was built (and how it differs from the original plan):** the original sketch sampled the live
price into a rolling buffer (Perf Monitor's model). That can't produce *retrospective* 1W/1Y/5Y history,
so we instead fetch **real candle history** from Finnhub's premium `/stock/candle` (+ `/crypto/candle`)
through a new provider seam — see the Finnhub candle spec + the candle layer files above. Concretely:
- `ChartRange` + `CandleInterval` (neutral), `DomainCandleSeries`/`CandlePoint`, `UiCandleSeries`,
  `Helpers/ChartHelper.cs` (ported SVG sparkline), `ApiFinnhubCandleDto`, and
  `IMarketDataProvider.GetCandlesAsync` → `MarketRepository.GetCandlesAsync` →
  `FinnhubMarketDataProvider.GetCandlesAsync` (+ `MockMarketDataProvider` synthetic series).
- The page body is a nested `SymbolChartForm : FormContent`: an adaptive card binding the SVG chart as
  an `Image`, with the 5 range tabs as `Action.Submit` buttons → `SubmitForm` switches range, fetches
  (per-range in-memory cache + a generation guard against fast taps), and updates `DataJson` to repaint.
  The first fetch fires from the page's "became visible" hook (the `INotifyItemsChanged.add`), so list
  rows building a `SymbolDetailPage` per item never trigger a fetch. Header price/%change reflect the
  **selected range** (last vs. first close), Robinhood-style — derived from the series, no extra `/quote`.
- **Gating:** this premium limit is **Finnhub-only**. With **Twelve Data** primary (its `/time_series`
  candles are free-tier), a free Twelve Data key renders real charts; only the **Finnhub fallback** path
  403s on a free key → the card shows "requires a paid Finnhub plan". To preview rendering with no key at
  all, temporarily point the repo at `new MarketRepository(new MockMarketDataProvider())` (don't ship —
  mock also feeds search).

**State of the two UI issues: flicker FIXED; the Enter/focus bug is UNSOLVED (left as-is, documented).**

1. **Flicker on range switch — ✅ FIXED.** Each `DataJson` write rebuilds the whole card
   (`ContentFormViewModel.RenderCard` → `AdaptiveCard.FromJsonString`; `ContentFormControl.DisplayCard`
   clears + re-adds children), and `Load()` used to write `DataJson` **twice** per tap (a "Loading…"
   state, then the result) → a double teardown/rebuild flash. **Fix:** `Load()` now only paints the
   "Loading…" card when **no chart is on screen yet** (the very first load); on later range switches it
   leaves the prior chart up and swaps it in place once the fetch lands. Tracked via `_displaySeries`
   (the currently-painted series; a real chart present ⇒ skip the loading write). Cached ranges already
   single-write.

2. **Enter activates "1D" instead of the primary command — ❌ UNSOLVED.** Root cause: with a single
   `FormContent`, CmdPal sets `OnlyControlOnPage = true` (`ContentPageViewModel`, `= (content.Count == 1)`)
   and **programmatically focuses the card's first focusable element** on load (`ContentFormControl.
   OnFrameworkElementLoaded` → `FindFirstFocusableElement().Focus()`) — the 1D `Action.Submit`. So Enter
   hits that tab, and with focus trapped in the card **Ctrl+Enter can't reach the secondary command
   either**. There is **no host API to suppress the auto-focus** — content count is the only documented
   lever. **Two fixes were tried and abandoned:**
   - **Option A — return a 2nd content item** (a hint-caption `MarkdownContent`) so `content.Count == 2`
     → `OnlyControlOnPage` should be false → focus should stay in the search box. The CmdPal **source
     reads this way**, but **in practice it did NOT help**: focus still landed on the card's first action
     (Enter still activated a tab) **and Ctrl+Enter stopped working too** — strictly worse. Why the
     observed behavior diverges from the source reading is unexplained (a lead for future investigation;
     not chased now). Reverted.
   - **Option C — lead the card with membership `Action.Submit` buttons** so the auto-focused first
     element is the watchlist toggle (Enter then add/removes). It worked but was rejected as too hacky and
     it splits list management away from the command bar (inconsistent with every other surface).
   **Current state:** back to the post-flicker-fix single-content card; membership stays on the command
   bar; the focus bug is **accepted as a known limitation**. Possible real fixes if revisited: a host API
   to set/suppress the focused element (doesn't exist today — would need an upstream CmdPal change), or
   moving range switching onto the command bar so the card has no focusable element at all.

For reference, the Perf Monitor source this chart was ported from
(`C:\Users\jarla\code\PowerToys\src\modules\cmdpal\ext\Microsoft.CmdPal.Ext.PerformanceMonitor\`):

**How Performance Monitor does it (the pattern to copy):**
- A band button's `Command` is itself an **`IContentPage`** (`WidgetPage : OnLoadContentPage`), so
  clicking the button **navigates into** the page. Same trick works for a list row — point the row's
  command at the content page.
- The page renders an **Adaptive Card** via a `FormContent` (`TemplateJson` = an adaptive-card template +
  `DataJson` = bound values); `GetContent()` returns `[formContent]`.
- **The chart is an inline SVG embedded as a data URI** — `ChartHelper.CreateImageUrl(values, type)`
  returns `"data:image/svg+xml;utf8," + <svg>…</svg>`, where the `<svg>` is just two `<polyline>`s (the
  line + a gradient-fill loop) and a border `<rect>`, built with `System.Xml.Linq`. **No
  System.Drawing/SkiaSharp** → a dependency-free, reflection-free SVG (now just good hygiene, not a hard
  requirement since AOT/trim is off — see the AOT/trim note). The card
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

**Refinements still open (flicker is fixed; the Enter/focus bug above is still open):**
- **Gridlines — ✅ DONE; numeric axis labels NOT POSSIBLE.** The chart draws a faint quarter-gridline box
  (the "normal graph" look) so the scale reads at a glance. **Numeric axis ticks were attempted and
  abandoned:** the SVG is rasterized by Direct2D's `ID2D1SvgDocument`, which **does not render `<text>`**
  (it silently drops it — lines/polylines/rects/gradients are fine), so `<text>` price/time labels never
  appeared (verified: gridlines showed, numbers didn't). The standard workaround — moving labels into the
  adaptive card around the image (a left price column + a bottom time `ColumnSet`) — was deliberately **not**
  pursued (alignment with the stretched image is fragile; deemed not worth the hack). Grid is decorative
  only, theme-neutral gray. (This is the same ceiling Perf Monitor's chart hits — it has no labels either.)
- **Active-tab highlight:** the tabs don't visually mark the selected range (adaptive cards can't easily
  restyle a button by bound data); the header caption names it instead.
- **Keyboard range switching:** you must Tab into the card to reach the tabs — no arrow-key switch from
  the search box. Entangled with the open Enter/focus bug: any real fix there (e.g. moving range
  switching onto the command bar, or host support for the focused element) would address this too.
- **Hover/scrub readout is NOT possible** on this surface: CmdPal extensions are out-of-process and the
  chart is a static image — no pointer/hover events cross the boundary (Perf Monitor's graph has the same
  ceiling; the content types are all declarative: Markdown / PlainText / Image / Form / Tree). Closest
  approximation = bake High/Low/Open/Close text + on-chart min/max labels (the `o/h/l/v` arrays are
  already parsed in `ApiFinnhubCandleDto`, just not yet promoted past `Close` into `CandlePoint`).
- **Live auto-refresh — ✅ DONE.** The open chart now re-fetches its **visible range** (not just 1D) on
  each `PollTicker` tick and repaints the `FormContent` in place, sharing the one ticker +
  `Publish().RefCount()` lifecycle with the priced list pages. `SymbolDetailPage` subscribes the chart in its
  visible-lifecycle hook (`replayOnSubscribe:false` — no double-fetch on open) and `SymbolChartForm.
  PollRefresh()` does the silent re-price: bypasses the per-range cache so the on-screen range actually
  refreshes, reuses the generation guard so a tab tap still wins, and keeps the last good chart on a
  transient empty poll. On the Finnhub fallback path candles are premium-gated (a free key re-fetches a
  403 each tick); with Twelve Data primary its free-tier candles refresh for real. See the "Done (this
  round): symbol-detail chart live refresh" bullet in Current Status.
- Not yet wired to a future `PortfolioPage` (doesn't exist yet).

### Asset logos as icons, app-wide (done)

Every instrument row, dock button, and the `SymbolDetailPage` header shows the instrument's **real logo**
(AAPL's apple, BTC's coin) instead of the generic github-favicon. Page **chrome** icons (list/search page
headers) still use the favicon.

**Shipped approach — Elbstream CDN, addressed by symbol (zero API calls).** Because `new IconInfo(url)`
makes the **CmdPal host** fetch the image, the logo URL is built *directly from the symbol* — so there is
**no API call, no caching layer, and no DTO**. `Helpers/AssetIconResolver.cs` is the single place the
convention lives:
- `Stock → https://api.elbstream.com/logos/symbol/{ticker}?format=png`
- `Crypto → https://api.elbstream.com/logos/crypto/{sym}?format=png`
- `Currency → https://api.elbstream.com/logos/country/{iso2}?format=png` — an FX pair shows its **base
  currency's country flag** (a small currency→ISO-3166 map, `EUR→eu`/`USD→us`/`JPY→jp`/…; the `eu` flag
  covers the Euro). Unmapped currencies and unknown categories fall back to a Segoe MDL2 glyph.

Our neutral `DomainInstrument.Symbol` matches Elbstream's identifiers directly (`AAPL`, `BTC`), so no
provider-specific symbol translation is needed. `format` ∈ svg/png/webp/jpg (default svg, PNG auto-fallback
when no SVG); `size` defaults to 100px. A **miss returns HTTP 404** (verified — not a placeholder image), so
an off-catalog/obscure symbol shows the host's empty-icon slot; the glyph fallback only covers whole
categories, **not** per-symbol misses (a true glyph-on-miss would need a per-symbol HEAD probe +
swap-on-arrival — deferred to keep the zero-call simplicity).

**Attribution (required).** Elbstream is free **with attribution**: a clearly-visible link back, min 12pt,
on any page where a logo shows — `<a href="https://elbstream.com">Logos provided by Elbstream</a>`. Placed as:
- a trailing clickable "Logos provided by Elbstream" row on the priced list pages (`PricedListPage.GetItems`,
  beside Refresh) and on Search results (`SearchPage.SearchItems`, only when results are present);
- a subtle `TextBlock` line **inside** the `SymbolDetailPage` adaptive-card template (deliberately NOT a 2nd
  `IContent` item — that would re-trigger the `OnlyControlOnPage` auto-focus bug);
- the **dock band is an accepted gray area** (no room for a link; arguably a "band," not a "page"). Strict-
  compliance escape if ever needed: keep the dock's generic icon so no un-attributed logo surface exists.

The credit row is clickable via `Commands/OpenUrlCommand.cs` → `ProcessHelper.OpenUrl(url)` (a shell launch,
not a captured `Run`). The shared row factory is `AssetIconResolver.AttributionRow()`.

**Wired in:** `SearchPage.BuildResultItem`, `WatchlistPage.BuildRow`, `FavoritesPage.BuildRow`,
`FavoritesDockPage.GetItems`, and the `SymbolDetailPage` ctor all set `Icon = AssetIconResolver.Resolve(...)`.

**Alternatives considered (why not):** Finnhub `/stock/profile2` (official `logo` URL, reuses our key, no
attribution) — but stocks-only and spends the tight ~300/day budget (one call per symbol, even with a
forever cache). Logo.dev (Clearbit's official successor, 500K/mo free) — needs a token → a new settings
field. Bundled static assets (e.g. `spothq/cryptocurrency-icons`) — offline/AOT-safe but only covers what
you ship. ⚠️ **Clearbit's free logo API was permanently sunset Dec 2025 — do not use it.** Elbstream won as
the only keyless, symbol-addressable source covering stocks + crypto + forex from one place; its attribution
requirement was the accepted trade-off.

**Future polish:** logos on a `PortfolioPage` when it lands (same `Resolve(...)` call); a glyph-on-miss probe
if blank slots on obscure symbols become annoying. (FX flags are now wired — Elbstream `/logos/country/{iso2}`
keyed off the base currency.)

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

> **`ContentPage` focus gotcha:** if `GetContent()` returns a **single** `FormContent`, the host marks it
> `OnlyControlOnPage = true` and **auto-focuses the card's first focusable element** (e.g. the first
> `Action.Submit`) on load — so Enter activates that card button instead of the page's primary (Enter)
> command — and with focus trapped in the card, Ctrl+Enter can't reach the secondary command either.
> Content count is the **only** documented lever (no host API suppresses the auto-focus). In theory:
> (a) return **≥2 content items** so `OnlyControlOnPage` is false and focus stays in the search box; or
> (b) **make the first focusable element the action you WANT Enter to fire** by ordering the card so that
> button comes first. ⚠️ The symbol-detail chart tried **both and neither shipped** — (a) did **not**
> actually keep focus in the search box in practice (Enter still hit a tab, Ctrl+Enter broke), and (b)
> was too hacky. It currently ships with the bug **unfixed**; full trace in the "Symbol detail + live
> chart" section of this file. Treat (a) as suspect until someone reproduces it working.

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

// Launch a URL / file / protocol with the OS default handler (UseShellExecute=true). Fire-and-forget;
// no output captured. Used by Commands/OpenUrlCommand.cs (the Elbstream attribution row).
ProcessHelper.OpenUrl(string url);
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
- **API key**: none needed at build time — each user pastes their own Finnhub key into the extension's settings at runtime (see the "API Key" section above).
- **Description**: update the placeholder description in `Package.appxmanifest`.

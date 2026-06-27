# Feature deep-dives

## Navigation / UX shape

One top-level **Markets** command (`MarketsPage`) — a hub that funnels into the screens:
**Search / Watchlist / Favorites / Portfolio / News / Data Sources / Settings**. (This replaced the old
three separate top-level commands, keeping the Command Palette root to one entry.) Sub-pages are built once
and reused, so their per-page caches survive navigating in and out of the hub.

**Enter on any instrument row opens the shared `SymbolDetailPage`** — the per-symbol detail screen
(detail + live chart), which is **the single place for list management**. Its command bar carries the
add/remove-watchlist (**Enter**) and add/remove-favorite (**Ctrl+Enter**) actions, plus portfolio
add/edit/remove in the overflow, labelled for the instrument's current state. The page subscribes to the
store flows so toggling there (the commands `KeepOpen`) flips the buttons in place. **List rows carry no
context actions** — they only navigate.

- **Search** (`SearchPage`) — the Enter-only `/search` flow (never per-keystroke, to protect rate limits).
- **Watchlist** (`WatchlistPage`) — tracked instruments, priced, grouped by class; a ★ marks favorites.
- **Favorites** (`FavoritesPage`) — the curated subset; **only favorites render in the dock band**.
- Watchlist + Favorites share `Pages/PricedListPage.cs`. Membership comes from `WatchlistStore`'s two
  flags `InWatchlist`/`IsFavorite`. Membership commands live in `Commands/MembershipCommands.cs` — each
  just mutates the store (the flows re-render every live surface) and shows a confirmation toast while
  keeping the palette open.
- **Data Sources** (`DataSourcesPage`) — a static informational screen (where data comes from,
  not-financial-advice, keys-stay-on-your-machine). Credited per-source; ECB credited here.

## Portfolio screen + dock band

**Markets Portfolio** tracks actual *holdings* (a quantity per symbol), priced live, with a totals summary
pinned on top and **daily P&L** + **total return** per holding. Reuses the data seam with no provider/
repository changes.

- `Settings/PortfolioStore.cs` — JSON holdings (`market_portfolio.json`); two `StateFlow`s
  `Positions`/`Instruments`, both **reference equality** so EVERY mutation re-emits (incl. quantity-only).
- `Models/DomainPosition` / `UiPosition` / `UiPortfolio` — `UiPosition` combines a `DomainQuote` + quantity
  (+ optional cost basis) → `MarketValue`, `DailyPnL` (qty × `DomainQuote.Change`), `TotalCost`/
  `TotalReturn`/`TotalReturnPercent`; `UiPortfolio.From` rolls up the totals. Formatting lives only here.
- **Daily P&L:** per position = qty × `DomainQuote.Change`. Aggregate **percent = totalDailyPnL /
  (totalValue − totalDailyPnL)** (vs yesterday's close), ÷0-guarded. `IsValid:false` holdings excluded.
- **Total return (cost basis):** per position = `MarketValue − qty × CostBasis`; **percent =
  (price − basis)/basis**. Shown only when a positive basis was recorded. Cost basis is stored per unit in
  the holding's **native currency**, so it converts with the same FX rate as the value.
- **Screen** (`PortfolioPage`) — a `PricedListPage` subclass, so it inherits all caching/polling/reconcile.
  The totals summary is the first row via `PricedListPage.LeadingRows(pricedQuotes)` (default empty → other
  pages unaffected), fed the full unfiltered priced set.
- **Editor** (`SetQuantityPage`) — a `ContentPage` with a quantity `Input.Number` (required, > 0) and an
  optional average-cost-per-unit `Input.Number`, both prefilled from the current holding. `SubmitForm` maps
  0/blank cost → null. The single-`FormContent` auto-focus quirk is helpful here (cursor lands in quantity).
- **Dock band** (`PortfolioDockPage`) — one summary button: total value + daily P&L rolled into the
  `PortfolioCurrency`; clicking opens `PortfolioPage`. Pure cache observer; `await`s `PrimeAsync` for the
  converted total in one paint.

## Multi-currency portfolio conversion

The `$`-everywhere assumption is gone. `DomainQuote` carries an ISO-4217 **`Currency`** (default `"USD"`);
providers stamp it (see `notes/providers.md`). The **Portfolio screen** values each holding in its native
currency **and** converts into the user's `PortfolioCurrency` via the keyless Frankfurter `/latest` rates
(`Helpers/CurrencyConverter.cs`).

- Two-phase because the screen renders synchronously: `PrimeAsync` batches every not-yet-fresh
  `native→preferred` rate in one call (~1 h TTL, negatives cached); `TryGetRate` is the synchronous cache
  read at render time. An all-USD portfolio with USD preferred does **zero** network (rate-1 short-circuit).
- ⚠️ **GBX/pence trap handled:** `CurrencyHelper.NormalizeStockQuote` folds London `GBp`/`GBX` to GBP
  (price & change ÷100). The Domain layer only ever sees major-unit prices in a major-unit code.
- A holding whose currency the ECB set can't convert is **excluded from the total** (surfaced as "*N* not
  converted"). `IsValid:false` holdings excluded as before.
- Beyond the portfolio, `UiQuote.FormatPrice` is currency-aware app-wide (a London stock shows `£2.50`).
- Caveat: the reporting currency is cached in-memory per session (not persisted); changing
  `PortfolioCurrency` applies on the next refresh/revisit (pull-style).

## Demo mode (offline testing)

A **`Demo mode` toggle** (`MarketSettingsManager`, key `demoMode`, default off) puts the whole app on
built-in sample data with **no API key and no network**.

- **Routing via an "exclusive provider" seam:** `IMarketDataProvider.IsExclusive` (default false);
  `MockMarketDataProvider` returns `DemoMode`. `MarketRepository.ActiveProviders()` returns **only the
  exclusive providers when any exist** — so quotes, candles, news, AND the search fan-out all route through
  the mock alone (zero network even with live keys set). `CurrencyConverter` fills its cache from a static
  `DemoUsdPerUnit` table.
- **Any symbol:** `Seed` (hand-tuned headline symbols incl. GBP `HSBA.L`) overrides on top of
  `SynthesizeQuote` — a stable FNV-1a hash (deterministic across refreshes AND restarts), category-/
  currency-inferred quote for any other ticker. Candles anchor on the same price.
- **Search matches a curated `Catalog` of ~120 REAL instruments, never fabricated.** Why: a watchlist/
  portfolio persists the full `DomainInstrument` to JSON, so a faked symbol would become a permanent blank
  row once demo mode is off.
- **Applies instantly:** `MarketSettingsManager.DemoModeChanged` (`StateFlow<bool>`) — every surface
  subscribes and resets at once (priced pages drop their cache, dock bands re-price, `CurrencyConverter`
  clears, hub/Search re-list the status row). The rate-limited banner is suppressed in demo mode.
- A blue **"Demo mode — showing sample data"** status row shows while it's on (`ApiKeyHint.StatusRow()`).

## Symbol detail + live chart (chart DONE; flicker fixed, Enter/focus bug open)

The shared `SymbolDetailPage` opens from any row (Enter) and every dock-button click, and renders a real
price chart with Robinhood-style range tabs (1D / 1W / 1M / 1Y / 5Y).

- Fetches **real candle history** through the provider seam (`GetCandlesAsync` → `DomainCandleSeries` →
  `UiCandleSeries`, SVG via `ChartHelper`). The page body is a nested `SymbolChartForm : FormContent`: an
  adaptive card binding the SVG `Image` with the 5 range tabs as `Action.Submit` buttons. Header price/%
  reflect the **selected range** (last vs first close), Robinhood-style.
- **Gating:** Finnhub candles are premium (free key → 403 → "requires a paid Finnhub plan"). **Twelve Data
  candles are free-tier** → a free TD key renders real charts. Demo mode draws synthetic candles.
- **Live refresh:** the open chart re-fetches its visible range each `PollTicker` tick (the only direct
  `PollTicker` subscriber left). Silent, bypasses the per-range cache, keeps last good on empty.

**Two UI issues:**
1. **Flicker on range switch — ✅ FIXED.** `Load()` only paints the "Loading…" card when no chart is on
   screen yet (first load); later switches leave the prior chart up and swap in place (tracked via
   `_displaySeries`).
2. **Enter activates "1D" instead of the primary command — ❌ UNSOLVED.** With a single `FormContent`,
   CmdPal sets `OnlyControlOnPage = true` and programmatically focuses the card's first focusable element
   (the 1D tab); focus trapped in the card also blocks Ctrl+Enter. No host API suppresses the auto-focus.
   Two fixes tried and abandoned (a 2nd content item — did NOT help in practice; leading the card with
   membership buttons — too hacky). Accepted as a known limitation. See `notes/cmdpal-toolkit.md` § focus.

**Known ceilings:** numeric axis labels NOT possible (Direct2D drops SVG `<text>`); no active-tab highlight
(adaptive cards can't restyle a button by bound data); no hover/scrub readout (out-of-proc, static image).

## Asset logos as icons (app-wide)

Every instrument row, dock button, and the `SymbolDetailPage` header shows the instrument's **real logo** via
the **Elbstream CDN, addressed by symbol** (`Helpers/AssetIconResolver.cs`) — because `new IconInfo(url)`
makes the **host** fetch the image, there is **zero API call, no caching, no DTO**:

- `Stock → /logos/symbol/{ticker}`, `Crypto → /logos/crypto/{sym}`, `Currency → /logos/country/{iso2}` (FX
  pair → base currency's flag, `EUR→eu`). Unmapped currencies/unknown categories → a Segoe MDL2 glyph.
- A miss returns HTTP 404 (host shows its empty-icon slot). Glyph fallback covers whole *categories*, not
  per-symbol misses.
- **Attribution required** (Elbstream is free with attribution): an `AssetIconResolver.AttributionRow()`
  "Logos provided by Elbstream" row on the priced pages + Search results, and a line inside the
  `SymbolDetailPage` card. The dock band is an accepted gray area. Credit row is clickable via
  `Commands/OpenUrlCommand.cs` → `ProcessHelper.OpenUrl`.
- ⚠️ Clearbit's free logo API was permanently sunset Dec 2025 — do not use it. Elbstream won as the only
  keyless, symbol-addressable source covering stocks + crypto + forex.

## Market News (feed)

A **Markets → News** screen (a hub row) lists market-news headlines, each opening the source article. News
flows through the SAME layered seams as quotes, with a parallel cache + orchestration.

- **Provider seam:** `SupportsNews => false` (a capability gate, NOT asset-class routing) +
  `GetNewsAsync(NewsCategory, minId, ct)` whose default body **THROWS** (unlike candles' soft opt-out —
  reaching it means a caller skipped the `SupportsNews` check; fail loud). Finnhub opts in;
  `MockMarketDataProvider` opts in only in Demo mode. Twelve Data / Frankfurter inherit the throwing default.
- **News cache** (`INewsCacheDataSource` / `InMemoryNewsCacheDataSource`): keyed by **`NewsCategory`** with
  a **LIST** of `NewsEntity` per category (news is inherently a list). DynamicData-backed, atomic `Edit`;
  keep-last-good = a transient EMPTY fetch won't wipe a non-empty cached feed. `Observe` emits **null until
  first fetch** (so a non-null EMPTY feed reads as "loaded-but-empty", distinct from "loading").
- **Repository:** `ObserveNews(NewsCategory)` (fixed) and `ObserveNews(StateFlow<NewsCategory?>)`
  (selection-aware, `Switch`), both `.ObserveOn(TaskPoolScheduler)` (the same deadlock seam). **The merged
  "All" view = the `NewsCategory?` selection where `null` = All** (`CombineLatest` the 4 categories →
  dedupe by id → newest-first). A **second poll loop** (`PollTicker.SubscribeNews`) on a separate
  `NewsRefreshInterval` (default 30 min). Stale-on-subscribe + demo-flip refill, as for quotes.
- **Screen** (`NewsPage`): a `DynamicListPage` **pure observer** with a `Filters` dropdown ("All" default +
  General/Forex/Crypto/Mergers); Enter opens the article (`OpenUrlCommand`); summary in the `Details` pane;
  typed search filters headlines client-side. No-key UX: a Finnhub-gated `ApiKeyHint.NewsStatusRow()` shows
  "News needs a Finnhub key" instead of spinning. Rate-limit banner inserted at the top.
- **News dock band** (`NewsDockPage`): a CNBC-style ticker, pure observer of the merged "All" feed.
  Renders a **row of 4 headline buttons** (each opens its own article) and **cycles** the window through the
  feed one headline per tick — NOT one scrolling Title (the host caps each dock button at `MaxWidth=100`≈15
  chars and won't pixel-scroll a Title). Cycle cadence = the **`News ticker speed (seconds)`** setting
  (`NewsTickerCycleSeconds`, default 60 s), re-read live each tick by a dedicated per-band
  `Observable.Generate` (NOT `PollTicker` — that's data-refresh; this is UI animation). Both subscriptions
  disposed on hide. Third `CommandItem` in `GetDockBands()` (`Id = com.costafotiadis.market.dock.news`).

**Left to do:** ① minId incremental paging (every refresh fetches `minId:0` and replaces the cached feed);
② multi-provider news (single-source today — `NewsProvider()` = first `SupportsNews`); ③ native review of
the machine-quality locale translations.

## Settings (`Settings/MarketSettingsManager.cs`)

`JsonSettingsManager` singleton → `…/Microsoft.CmdPal/market.settings.json`. Holds: the API keys
(`FinnhubApiKey`/`TwelveDataApiKey`), the price **refresh interval** (`RefreshMinutes`, default 10, 0=off →
`RefreshInterval`/`AutoRefreshEnabled`), the **`News refresh interval (minutes)`** (`NewsRefreshMinutes`,
default 30 — deliberately separate from the price interval), the news ticker **`News ticker speed
(seconds)`** (`NewsTickerCycleSeconds`, default 60), `PortfolioCurrency`, the **`Demo mode`** toggle
(→ `DemoMode` + the observable `DemoModeChanged`), and **`Show rate-limit warnings`** (`ShowRateLimitErrors`,
default on). The same form is shown both in CmdPal's Settings UI and as a hub row.

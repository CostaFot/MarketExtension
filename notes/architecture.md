# Market Data Architecture (deep reference)

The core of the app. Live quotes flow through three explicitly-named layers + a coordinator.
**Keep this layering and naming for new code.**

```
ApiFinnhubQuoteDto ─(provider maps)→ DomainQuote ─(repository routes+merges)→ IReadOnlyList<DomainQuote>
                                                          └─(page maps via UiQuote.From)→ UiQuote (rendered)

ApiFinnhubCandleDto ─(provider maps)→ DomainCandleSeries ─(repository routes)→ DomainCandleSeries
                                                          └─(page maps via UiCandleSeries.From)→ UiCandleSeries (SVG chart)
```

News mirrors the same shape: `ApiFinnhubNewsDto[] → DomainNews → (cache) → UiNews`.

## Naming convention

- `Api*` — raw provider DTOs (provider-specific), e.g. `ApiFinnhubQuoteDto`.
- `Domain*` — provider-agnostic, **no formatting**; what every provider AND the repository return
  (`DomainQuote`, `DomainInstrument`, `DomainCandleSeries`, `DomainNews`, `DomainPosition`).
- `*Entity` — data-layer **storage models** that live BELOW the repository, INSIDE a data source
  (`QuoteEntity`, `NewsEntity`). A structural mirror of the matching `Domain*` that **never escapes its
  data source**: the repository maps `Domain* ⇄ *Entity` at its boundary (`QuoteEntity.From(domain)` on
  write, `entity.ToDomainQuote()` on read). Lets the stored shape diverge later (storage-only fields, a
  DB representation) without touching domain or surfaces. (Data → domain is the allowed dependency
  direction, so an `*Entity` may reference its `Domain*` for mapping — never the reverse.)
- `Ui*` — presentation; the ONLY place `FormatPrice()`/`FormatChange()`/SVG/relative-time live
  (`UiQuote`, `UiCandleSeries`, `UiNews`, `UiPosition`, `UiPortfolio`).
- `AssetCategory`, `ChartRange`, `CandleInterval`, `NewsCategory` (enums) stay **unprefixed** — shared
  vocabulary across all layers (they classify/parameterize operations; they aren't data carried through).

## File map

| File | Role |
|---|---|
| `Data/MarketRepository.cs` | **The coordinator the UI depends on**, and the **orchestration layer** that owns the shared quote cache + polling. Routes each `DomainInstrument` to the first `IMarketDataProvider` whose `Supports(AssetCategory)` matches, fans out concurrently, merges into one order-preserving list. Also `SearchAsync(query)`, `GetCandlesAsync(instrument, range)`, and the News orchestration (`ObserveNews`). No provider for a category → `IsValid:false` quote / invalid candle series. **`ActiveProviders()`**: any `IsExclusive` provider (the Demo mock) → ALL operations route to it alone. **Owns one `IQuoteCacheDataSource`** (in-memory by default; injectable ctor for a future DB-backed cache): `RefreshAsync` **writes through** every fetch, and **`ObserveQuotes(...)`** returns a cache-backed `IObservable<IReadOnlyList<DomainQuote>>` — for a fixed instrument list OR a membership `StateFlow` (Rx `CombineLatest` + `Switch`), **delivered via `ObserveOn(TaskPoolScheduler)`** (the deadlock fix — see below). **Owns the single poll loop:** one process-lifetime `PollTicker` subscription whose `RefreshObserved` re-fetches the union of currently-OBSERVED instruments each tick ("observed" = a refcounted registry that `ObserveQuotes` Register/Unregisters on subscribe/dispose). A second `SubscribeNews` loop polls observed news categories on the news cadence. The demo-flip handler `Clear()`s the cache then refills from the new source. |
| `Data/IQuoteCacheDataSource.cs` | The **shared observable quote cache** abstraction (an interface, swappable for a DB-backed impl later). **Stores `QuoteEntity`, NOT `DomainQuote`** — the repo maps at its boundary. `Get(symbol)` (sync snapshot, null if uncached), `Observe(symbol)` → per-symbol `IObservable<QuoteEntity?>` (replays current value, **null until first fetch**), `Upsert(quote, keepLastGood=true)` (write-through; **the SINGLE home for keep-last-good**), `Clear()`. Keyed by `WatchlistStore.Normalize`. |
| `Data/InMemoryQuoteCacheDataSource.cs` | The **real, live** in-memory impl, backed by **DynamicData's `SourceCache<QuoteEntity, string>`** (a reactive keyed cache — the .NET analog of Room's `@Query → Flow`). `Get` = `_cache.Lookup(key)`; `Observe` = `Connect().Watch(key)` mapped to `QuoteEntity?` (Remove → null) + `StartWith(Get())` + `DistinctUntilChanged`; `Clear` = `_cache.Clear()`. **`Upsert` is `_cache.Edit(...)`: read + keep-last-good decision + write run ATOMICALLY under the cache's lock** — this is why the cache owns keep-last-good correctly under concurrent write-throughs (poll loop, observe-subscribe fetch, demo flip), which the old "decide under the lock, `Update` outside it" split could not. (Needs `DynamicData`; it floors `System.Reactive` at 6.1.0.) |
| `Data/QuoteEntity.cs` | The cache's **storage model** (`*Entity` layer): a structural mirror of `DomainQuote` that never escapes the data source. `QuoteEntity.From(domain)` / `entity.ToDomainQuote()`. |
| `Data/IMarketDataProvider.cs` | ONE data source: `bool Supports(AssetCategory)` + **`bool IsExclusive`** (default `false`; true → route every op to this provider alone — how the Demo mock takes precedence) + `GetQuotesAsync` + `SearchAsync` (free-text → `DomainInstrument`s, identity only, no prices; can't-search → `[]`) + `GetCandlesAsync(instrument, ChartRange, ct)` (**default interface method** returns an invalid series → non-candle providers opt out for free) + **`bool SupportsNews => false`** + **`GetNewsAsync(NewsCategory, minId, ct)`** (default body **THROWS** `NotSupportedException` — news has the `SupportsNews` gate, so reaching the default means a caller skipped the check; fail loud). |
| `Data/TwelveData/TwelveDataMarketDataProvider.cs` | **Primary provider when its key is set** (Stock+Crypto+Currency, gated on `HasTwelveDataApiKey`). One API for all three; **`/time_series` candles are free-tier**. `GetQuotesAsync` batches all symbols into one `/quote`. See `notes/providers.md`. |
| `Data/Finnhub/FinnhubMarketDataProvider.cs` | Stock+Crypto provider, used when no Twelve Data key is set. Also the **news** provider (`SupportsNews => true`, `/news`). Maps `ApiFinnhubQuoteDto` → `DomainQuote`; `/search` is US equities only. |
| `Data/Frankfurter/FrankfurterMarketDataProvider.cs` | **Keyless** FX provider (Currency) via ECB daily rates (`api.frankfurter.dev`). Day-over-day change; daily-close candles; `SearchAsync` = local filter over the catalog's FX pairs. |
| `Data/Frankfurter/ApiFrankfurterDto.cs` | Time-series DTO + flat `/latest` DTO (used by `CurrencyConverter`) + `FrankfurterJsonContext`. |
| `Data/MockMarketDataProvider.cs` | The **Demo-mode** data source — quotes/candles/news for every asset class, no key/network. Registered **first**; `Supports()`/`IsExclusive`/`SupportsNews` gated on the `DemoMode` setting → in demo mode the repo routes everything to the mock alone. **Serves ANY symbol** via a hand-tuned `Seed` over a stable-hash `SynthesizeQuote`. **Search** matches a curated `Catalog` of ~120 *real* instruments (never fabricated). See `notes/features.md` § Demo mode. |
| `Data/InstrumentCatalog.cs` | Static `DomainInstrument` defaults — the **first-run seed** for `WatchlistStore`. |
| `Settings/WatchlistStore.cs` | JSON-persisted tracked instruments, each carrying **two flags** `InWatchlist`/`IsFavorite` (favorites = the dock subset). Stores **full `DomainInstrument` identity** so searched non-catalog symbols re-price. Source-gen `WatchlistJsonContext` → `market_watchlist.json`; seeds `InstrumentCatalog` on first run. Exposes `Watchlist`/`Favorites` subsets as observable `StateFlow`s; pages and the dock subscribe and re-render. |
| `Settings/PortfolioStore.cs` | JSON-persisted holdings (`market_portfolio.json`), modeled on `WatchlistStore`. Each entry = instrument + quantity (`decimal`) + optional cost basis. **No first-run seed.** Two `StateFlow`s: `Positions` and `Instruments` — both **reference equality (NOT `InstrumentListComparer`)** so EVERY mutation (incl. a quantity-only edit) re-emits. |
| `Helpers/StateFlow.cs` | A tiny **Kotlin-StateFlow analog**, a **thin wrapper over `System.Reactive`'s `BehaviorSubject<T>`**. Used by the state holders (`WatchlistStore`, `PortfolioStore`, `MarketSettingsManager`). `StateFlow<T>` (read-only `Value` + `Subscribe` with replay-on-subscribe + distinct-until-changed; a `replayOnSubscribe:false` overload). `MutableStateFlow<T>` (`Update`). `SetValue` does source-side distinct-until-changed. `AsObservable()` exposes the raw stream for Rx `CombineLatest`/`Switch`. Plus `InstrumentListComparer`. |
| `Helpers/PollTicker.cs` | The **poll ticker** — **pure Rx** (`Observable.Generate(...).Publish().RefCount()`). Generalized into a parameterized `BuildTicker` exposing TWO independent, refcounted tickers: **`Subscribe`** (price, `RefreshInterval`) and **`SubscribeNews`** (news, `NewsRefreshInterval`). Per-step delay re-read from settings each iteration (interval/on-off applies without reload; `0` = off idles on a 30 s re-check, tick filtered out). `RefCount()` starts on first subscriber, tears down on last. `Subscribe(...)` wraps the handler in try/catch so a throwing tick can't tear down the shared multicast. **`MarketRepository` holds the only quote subscriber** (the central poll loop); the **only direct subscriber left is the symbol-detail chart** (separate candle path, no cache). |
| `Helpers/HttpRetry.cs` | **Shared 429 back-off** at the HTTP seam. `SendAsync(send, tag, ct)` takes a request thunk and, on `429`, honors a short `Retry-After` else backs off `1s`→`2s` (max 3 attempts, **bails** if the wait would exceed `8s` — per-minute windows don't clear in seconds). Single choke point feeding `RateLimitSignal` (2xx → off, surviving 429 → on). Used by Finnhub + Twelve Data (not keyless Frankfurter). |
| `Helpers/RateLimitSignal.cs` | Process-wide "are we throttled" flag — `StateFlow<bool>`. Priced surfaces subscribe and re-render. Intentionally **global, not per-symbol** (a free-tier limit is key-wide). |
| `Helpers/RateLimitHint.cs` | The amber "Rate-limited — showing last known prices" banner row (Enter = `NoOpCommand`), shown when `RateLimitSignal` is set AND `ShowRateLimitErrors` is on AND not in demo mode. Pinned to the top of the priced pages / Search / News. |
| `Helpers/ApiKeyHint.cs` | Unified **`StatusRow()`** (demo on → blue "Demo mode — showing sample data"; else no key → red "No API key set"; else null) + a Finnhub-gated **`NewsStatusRow()`** ("News needs a Finnhub key"). |
| `Helpers/DataSourceAttribution.cs` | The **active live provider's** attribution row/text (Twelve Data required; Finnhub as good practice). Reflects the ACTIVE source: demo → null, TD key → Twelve Data, else Finnhub key → Finnhub, else null. Clickable `OpenUrlCommand`. ECB (Frankfurter) and Elbstream are credited separately. |
| `Helpers/AssetIconResolver.cs` | Instrument identity → row/dock `IconInfo` via **Elbstream** logo URLs by category (`/logos/symbol/{t}`, `/logos/crypto/{c}`, FX → base currency flag `/logos/country/{iso2}`); Segoe-glyph fallback. **Zero API calls** (the host fetches the URL). Also `AttributionRow()` (the Elbstream credit). |
| `Helpers/CurrencyConverter.cs` | Process-wide singleton converting money via Frankfurter's keyless `/latest` spot rates. Two-phase: **`PrimeAsync(preferred, natives)`** batches every `native→preferred` rate (~1 h TTL); **`TryGetRate(from, to)`** is the synchronous cache read used while rendering. Used only by the Portfolio surfaces. In demo mode, fills from a static `DemoUsdPerUnit` table. |
| `Helpers/CurrencyFormat.cs` | Render a `decimal` as money in an ISO-4217 currency — the home for the per-currency symbol + decimal-place rules. `Format`/`FormatSigned`. JPY/KRW 0-decimal; unknown → trailing code. |
| `Helpers/CurrencyHelper.cs` | Currency conventions the **providers** apply when stamping `DomainQuote.Currency`: `NormalizeStockQuote` folds London **GBX/GBp pence → GBP** (÷100; the 100× trap); `QuoteCurrencyOfPair` returns an FX pair's quote currency. |
| `Helpers/ChartHelper.cs` | Perf-Monitor's **SVG-sparkline-as-`data:`-URI** (pure `System.Xml.Linq`), generalized to N points + min/max Y-normalize, green/red. Faint quarter-gridline box. **No numeric tick labels** — the host rasterizes via Direct2D's `ID2D1SvgDocument`, which silently drops `<text>`. |
| `Helpers/Strings.cs` | Localization helper — `Strings.Get`/`Strings.Format` over `Properties/Resources.resx`. **Don't hardcode user-facing literals**; add a resx key. The neutral resx is English; ship a language by adding `Resources.<culture>.resx`. |
| `Helpers/Log.cs` | `Log.Info/Warn/Error(tag, message)`, all `[Conditional("DEBUG")]` → compiled out of Release (the MSIX ships silent, no telemetry). |
| `Data/IQuoteCacheDataSource` + news equivalents | `Data/INewsCacheDataSource.cs` / `InMemoryNewsCacheDataSource.cs` — the news analog, keyed by **`NewsCategory`** with a **LIST** of `NewsEntity` per category. keep-last-good here = a transient EMPTY result won't wipe a non-empty cached feed. `Observe` emits **null until first fetch**. `Data/NewsEntity.cs` is the storage model. |
| `Models/*` | `AssetCategory`, `DomainInstrument`, `DomainQuote` (carries `Currency`, default `USD`), `UiQuote`; `ChartRange`+`CandleInterval`; `DomainCandleSeries`/`UiCandleSeries`; `DomainNews`/`NewsCategory`/`UiNews`; `DomainPosition`/`UiPosition`/`UiPortfolio`. |
| `Pages/*` | `MarketsPage` (the hub), `SearchPage`, `WatchlistPage`/`FavoritesPage`/`PortfolioPage` (the `PricedListPage` trio), `NewsPage`, `DataSourcesPage` (static info), `SymbolDetailPage` (detail + chart), `SetQuantityPage` (holding editor), `FavoritesDockPage`/`PortfolioDockPage`/`NewsDockPage` (the three dock bands). |
| `Commands/*` | `MembershipCommands` (watchlist/favorites add/remove + toast + KeepOpen), `PortfolioCommands`, `OpenUrlCommand`. |

## Shared quote cache + repository orchestration (DONE — all quote surfaces migrated)

**Why.** Every priced surface used to fetch the **same** quotes independently and keep its **own** cache +
its **own** `PollTicker` subscription. Their fetches landed at different times, so two surfaces showing the
same symbol drifted out of sync. The fix: one shared observable cache that every surface observes, with the
repository orchestrating all fetching/polling.

**The `ObserveOn` delivery seam (the deadlock fix — do NOT remove).** Both `ObserveQuotes` overloads append
**`.ObserveOn(TaskPoolScheduler.Default)`** (the raw graph lives in a private `ObserveQuotesCore`; the
`StateFlow` overload `ObserveOn`s *after* `Switch`). Why load-bearing: a cache write-through fans out
synchronously, and `CombineLatest`/`Switch` call the subscriber's handler — ultimately a surface's
`RaiseItemsChanged`, a **blocking COM call into Command Palette's STA** — *while Rx still holds the combiner
gate lock*. The host re-enters the extension and the gate↔STA lock order cycles → **CmdPal hangs** (exactly
what made the favorites band crash the host). `ObserveOn` hands each emission to the scheduler, so surfaces
are notified only **after** the gate locks release. ⚠️ **`SubscribeOn` is NOT a substitute** — it moves
*where you subscribe*, not *where notifications fire*. Consequence: **`OnQuotesChanged`-style handlers run on
a pool thread** — don't wrap the subscribe in `Task.Run` or add your own `SubscribeOn`/`ObserveOn`.

**The single poll loop.** The repo holds ONE process-lifetime `PollTicker.Subscribe(RefreshObserved)`. Each
tick refreshes the **distinct union of currently-observed instruments** in one batched fetch. "Observed" = a
refcounted registry (`_observed`) that `ObserveQuotes` Registers on subscribe and Unregisters on dispose, so
a hidden surface's symbols stop being polled, and a symbol observed by two surfaces is fetched once. Carrying
the full `DomainInstrument` (not just the symbol) is deliberate: routing needs `Category`.

**Stale-on-subscribe (the central freshness clock).** Every write-through stamps a per-symbol last-fetch
time (`_lastFetchTicks`). On subscribe, `ObserveQuotesCore` refreshes symbols `NeedsFetchOnSubscribe` flags:
**never-cached** ones always, plus — only when `AutoRefreshEnabled` — ones whose cached price **aged ≥ one
`RefreshInterval`** while unobserved. That's what makes a hidden surface refresh on reopen instead of showing
a stale price until the next tick.

**Demo-mode flip** is centralized: the repo's `DemoModeChanged` handler `Clear()`s the cache then
`RefreshSafe`es the observed set from the new source.

**Migrated:** both dock bands AND the `PricedListPage` trio are **pure observers** now.
`FavoritesDockPage` is the simple template (its whole lifecycle is one `ObserveQuotes(Favorites).Subscribe`).
`PortfolioDockPage` is the **async-projection template**: it `await`s `CurrencyConverter.PrimeAsync` before
rolling up `UiPortfolio` (one paint). The trio migrated via the shared base `PricedListPage`: a single
`Repository.ObserveQuotes(_instruments).Subscribe(...)`, with the old per-surface fetch/poll machinery
deleted. The async-projection seam is `protected virtual Task OnQuotesProjectingAsync(quotes)` (default
no-op; `PortfolioPage` overrides it to `await PrimeAsync`). The manual **Refresh 🔄** row reroutes onto
`RefreshAsync(keepLastGood:false)` and owns its own spinner.

⚠️ **Out-of-order race in the async-projection observe path (fixed).** On a cold cache `ObserveQuotes` emits
progressive *partial* snapshots. The handler used to be fire-and-forget (`.Subscribe(q => _ = OnAsync(q))`),
so for surfaces that `await` an async projection the lambda returned at the first `await` → two handlers ran
concurrently and a stale partial could finish LAST and overwrite the full total. **Fix:** project each
emission through `Select(q => Observable.FromAsync(ct => OnQuotesChangedAsync(q, ct))).Switch()` — `Switch`
cancels the prior projection's `ct` the instant a newer emission arrives; the handler checks `ct` before
painting. Applied in `PricedListPage` + `PortfolioDockPage`. Watchlist/Favorites are unaffected (their
projection is `Task.CompletedTask`, never yields).

**Transitional truth.** Every priced QUOTE surface observes the cache. The only surface still on its own
`PollTicker` subscription is the symbol-detail **chart** — a separate **candle** path (`GetCandlesAsync`)
with no cache/observe layer, left that way deliberately (only one detail page is ever open → no drift).

## Live price polling

> Superseded for all quote surfaces by the orchestration above — the priced pages + dock bands no longer
> poll themselves. The per-surface model now describes **only the symbol-detail chart**.

Default **10 min**, settings-configurable (0 = off). Built on `PollTicker`'s `Publish().RefCount()`
lifecycle. The chart's `SymbolChartForm.PollRefresh()` re-fetches its **visible range** each tick: silent
(no flicker), bypasses the per-range cache, reuses the generation guard so a tab tap still wins, keeps the
last good chart on a transient empty poll.

⚠️ **Rate-limit tension.** Finnhub free tier ~60 calls/min AND ~300/day, and `GetQuotesAsync` issues one
`/quote` per instrument. Polling N instruments at interval T burns the budget fast at low T. Mitigations in
place: the configurable interval + **Off** (0), polling only while visible, keep-last-good, 429 back-off +
banner, and Twelve Data's batched `/quote`.

## Dock refresh on favorites change

A pinned `FavoritesDockPage` updates the instant a favorite changes anywhere. `WatchlistStore.Favorites` is
a `StateFlow`; every favorite flip re-publishes it (deduped by `InstrumentListComparer`). The band observes
`ObserveQuotes(Favorites)` and the `Switch` overload re-projects on membership change. Complementary to
polling (which catches *price* drift on a timer); this pushes *membership* changes immediately and still
works when polling is Off.

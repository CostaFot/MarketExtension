# Status log / changelog

Historical "what shipped each round" notes, most-recent first. Full verbatim detail (with every decision
trace) is in `notes/CLAUDE_copy.md`; this is the condensed index. Design details for each feature live in
`notes/architecture.md`, `notes/features.md`, and `notes/providers.md`.

## Shipped & live-verified

- **News dock band** — a CNBC-style ticker (row of 4 cycling headline buttons; the host won't pixel-scroll a
  Title). Cycle cadence = the `News ticker speed (seconds)` setting. ✅ Live-verified on-device.
- **News feature polish** — no-key/empty-state UX (Finnhub-gated `NewsStatusRow`; cache emits null until
  first fetch) + rate-limit banner on the news page. ✅ News path live-verified via the dock band.
- **Quote cache on DynamicData `SourceCache`** — `InMemoryQuoteCacheDataSource` re-backed on DynamicData;
  `Upsert` = atomic `_cache.Edit`, fixing the keep-last-good race for free. ✅ Live-verified.
- **Out-of-order race fix** in the async-projection observe path (Portfolio screen + dock band) — replaced
  fire-and-forget with `Select(FromAsync).Switch()` + a `ct` guard before painting. ✅ Live-verified.
- **`PricedListPage` trio → pure cache observers** — Watchlist/Favorites/Portfolio migrated; per-surface
  fetch/poll machinery deleted; the quote-cache migration is COMPLETE (only the candle chart stays off-cache).
- **`PortfolioDockPage` → pure observer** + the stale-on-subscribe freshness primitive in the repo.
- **Shared quote cache + repository orchestration** — the de-drift refactor; `FavoritesDockPage` migrated +
  verified (the build that confirmed the `ObserveOn` deadlock fix). ✅ Live-verified.
- **Removed Sentry entirely** — the app ships with NO telemetry; `Log.Error` is now `[Conditional("DEBUG")]`.
- **Demo mode** — offline sample data with no key/network via the `IsExclusive` provider seam; applies
  instantly across the app (`DemoModeChanged`); serves ANY symbol (`SynthesizeQuote`) + curated real-ticker
  search `Catalog`; no rate-limit banner while demoing. ✅ Live-verified.
- **Cost-basis / total-return reporting** — captured in `SetQuantityPage`, surfaced on rows/totals/dock band.
  ✅ Live-verified (GBP `HSBA.L` with a basis).
- **Portfolio dock band** — total value + daily P&L rolled into `PortfolioCurrency`. ✅ Live-verified.
  (⚠️ glyph-icon gotcha hit — see CLAUDE.md.)
- **Multi-currency portfolio conversion** — `DomainQuote.Currency` + `CurrencyConverter` (keyless ECB);
  GBX/pence ÷100. ✅ Live-verified (10 sh SPY in GBP → ~£5,571, a real conversion).
- **Rate-limit (429) back-off + banner** — `HttpRetry` / `RateLimitSignal` / `RateLimitHint`. ✅
  Live-verified.
- **Twelve Data provider — now primary** — one API for stocks+crypto+forex, **free-tier candles**. ✅
  Verified live.
- **Migrated the observable layer to Rx.NET** — `StateFlow` → `BehaviorSubject` wrapper; `PollTicker` →
  pure Rx `Observable.Generate(...).Publish().RefCount()`. ✅ Live-verified.
- **Asset logos as icons app-wide** — Elbstream CDN, zero API calls; Elbstream attribution rows.
- **Demo mode serves ANY symbol** — `SynthesizeQuote` (stable FNV-1a hash) + curated `Catalog` search.
  ✅ Live-verified.
- **Symbol-detail chart** — real candle history with 1D/1W/1M/1Y/5Y tabs; live refresh on the ticker;
  flicker fixed. ⚠️ Enter/focus bug is an open known limitation.
- **Live price polling**, **FX provider (keyless Frankfurter)**, **Portfolio screen**, the **three-screen
  UX** + observable (StateFlow) data layer, **stale-revisit catch-up**, runtime API key + refresh-interval
  settings, Enter-only Finnhub `/search`, persistent watchlist + favorites. ✅ Verified.

## Open / deferred

- **News:** ① minId incremental paging (every refresh fetches `minId:0` and replaces the feed); ② multi-
  provider news (single-source today); ③ native review of machine-quality locale translations.
- **Symbol-detail chart Enter/focus bug** — UNSOLVED (host auto-focuses the card's first action; no API to
  suppress it). Two fixes tried and abandoned. See `notes/cmdpal-toolkit.md`.
- **Per-symbol error UX** (a typed `QuoteStatus` on `DomainQuote`) — deliberately NOT built; the rate-limit
  signal is global (a free-tier limit is key-wide).
- **Crypto/FX symbol search on the keyless fallback path** — Finnhub `/search` is US-equities only; FX via a
  local catalog filter; crypto search needs a TD key (TD's `/symbol_search` covers all three classes).
- **Candle cache layer** — possible but unplanned (only one detail page is open at a time → no drift).

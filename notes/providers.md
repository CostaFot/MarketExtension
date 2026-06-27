# Providers (data sources)

Providers are registered in `MarketExtensionCommandsProvider` in this order — first-match by asset class:

```csharp
new MarketRepository(
    new MockMarketDataProvider(),       // gated on Demo mode → wins everything when demoing
    new TwelveDataMarketDataProvider(), // primary WHEN its key is set (gated on the key)
    new FinnhubMarketDataProvider(),    // stocks + crypto fallback; also the news provider
    new FrankfurterMarketDataProvider() // keyless ECB forex
)
```

## To add a provider

Implement `IMarketDataProvider`:
- `Supports(AssetCategory)`, `GetQuotesAsync`, `SearchAsync` (return `[]` if it can't search).
- Optionally **override `GetCandlesAsync`** to serve chart history, else the default invalid-series opt-out
  applies. The provider translates the neutral `ChartRange.Interval`/`Lookback` into its own API tokens
  (Finnhub's `ToFinnhubResolution` is the model; TD maps `CandleInterval` to `5min/30min/1h/1day/1week`).
- For news, set `SupportsNews => true` and implement `GetNewsAsync`.

Map its `Api*` DTO → `DomainQuote` (and → `DomainCandleSeries` for candles), register it in
`MarketExtensionCommandsProvider`, gate its `Supports()` on whatever should make it active. **Zero UI/
repository changes.** Worked examples: `FrankfurterMarketDataProvider` (keyless FX) and
`TwelveDataMarketDataProvider` (an all-three-classes source) were both added exactly this way.

## Twelve Data (primary provider)

- Base `https://api.twelvedata.com/`, key via `apikey=` query param (setting `TwelveDataApiKey`). **One
  source for stocks + crypto + forex.** Registered FIRST among live providers; `Supports()` gated on the
  key → a no-key install falls through to Finnhub/Frankfurter.
- Free tier **~8 credits/min, ~800/day** (1 credit/symbol) — tighter per-minute than Finnhub. So
  `GetQuotesAsync` **batches every instrument into ONE `/quote` call** (comma-joined). Response shape
  differs by count: **>1 symbol** → object keyed by symbol (`Dictionary<string, ApiTwelveDataQuoteDto>`);
  **1 symbol** → a bare quote object. A global error (bad key/429) can come back as a bare
  `{"status":"error"}` even on HTTP 200 → the parse degrades to all-invalid.
- Symbol formats: stock `AAPL`, crypto `BTC/USD`, FX `EUR/USD`. `ToTwelveDataSymbol` maps our neutral
  symbols; `SearchAsync` normalizes results **back** (crypto → base coin, FX → 6-letter) so they round-trip.
- **Candles are FREE-tier** — the headline win: `GET /time_series?symbol=&interval=&outputsize=` renders
  the symbol-detail chart on a free key (Finnhub's candles are premium → 403). Values arrive
  **newest-first** → the provider reverses to oldest-first. Numbers are JSON **strings** →
  `NumberHandling=AllowReadingFromString`.
- **Search** `/symbol_search?symbol=` returns identity only across all three classes; `instrument_type`
  → `AssetCategory`.
- **Currency:** `currency` rides in the batched `/quote` (zero extra calls). Stamped per category: stock →
  the field (normalized), crypto → USD, FX → the pair's quote currency.

## Finnhub

- Base `https://finnhub.io/api/v1`, endpoint `/quote`. Free tier ~60 calls/min, ~300/day. Setting
  `FinnhubApiKey`; `SearchAsync`/quote/candle short-circuit to "no data" (with a `Log.Warn`) when unset.
- **No forex on the free tier** — `OANDA:*` returns 403. FX is routed to keyless Frankfurter.
- Symbol formats: stock = bare (`AAPL`), crypto = `BINANCE:{SYM}USDT`, FX = `OANDA:{BASE}_{QUOTE}`.
- An all-zero `/quote` response = invalid/unknown symbol → `IsValid:false`.
- **Candles — PREMIUM** (free key → 403): `GET /stock/candle?symbol=&resolution=&from=&to=` (crypto:
  `/crypto/candle`). `resolution` ∈ `1,5,15,30,60,D,W,M`; `from`/`to` are UNIX seconds. Response = parallel
  arrays `c/h/l/o/t/v` + `s`. `GetCandlesAsync` maps each `ChartRange`: 1D→`5`/1d, 1W→`30`/7d, 1M→`60`/31d,
  1Y→`D`/365d, 5Y→`W`/5y. 403/429/`no_data` → invalid series → the chart shows "unavailable".
- **Symbol search** `/search?q=&exchange=US` (free tier): identity only, **no prices**, scoped to **US
  equities** (mapped to `Stock`). UI is **Enter-only** (never per-keystroke) to protect the rate limit.
- **Currency:** `/quote` has no currency, so stocks resolve via `/stock/profile2?symbol=`, cached
  **per-symbol forever** (one extra call per *new* stock per session; the USD fallback on failure is cached
  too). Crypto short-circuits to USD; FX isn't served here. `ApiFinnhubProfileDto.cs`.
- **News:** `GET /news?category=&minId=&token=` returns a **bare top-level array** (`ApiFinnhubNewsDto[]`
  registered as the root on `FinnhubJsonContext`). `category` ∈ `general/forex/crypto/merger` (the only
  place that token lives is `ToFinnhubNewsCategory`); `minId` returns only items newer than that news id
  (0 = latest batch). Drops items missing id/headline/url at the boundary.

## Frankfurter (FX)

- Base `https://api.frankfurter.dev/v1/`. **Keyless, no documented rate limit** → no key gate.
- Wraps the **ECB daily reference rates** → **daily only** (one fixing per business day, ~16:00 CET).
  Implications: a quote's **change is day-over-day** (latest vs previous fixing); charts plot **daily
  closes** (1D/1W tabs show recent fixings, not intraday; the candle lookback is floored at 7 days).
- **One endpoint for both flows** — the time series `GET /{from}..{to}?base={BASE}&symbols={QUOTE}` →
  `{ "rates": { "2024-06-03": { "USD": 1.0842 }, … } }`. Quotes fetch a ~10-day window and take the last
  two points; candles fetch the range's lookback. Any base works (cross-converts via EUR).
- Pairs are the neutral **6-letter `BASE+QUOTE`** (`EURUSD`); the provider splits 3+3. `SearchAsync` is a
  **local filter over `InstrumentCatalog`'s FX pairs** (Frankfurter has no symbol-search endpoint).
- Icons: an FX pair shows its **base currency's country flag** (Elbstream `/logos/country/{iso2}`, EUR→`eu`).

## AOT/trim — intentionally OFF

This is a hobby project that does **not** trim or Native-AOT compile — it ships **self-contained
single-file JIT** (see `MarketExtension.csproj`). Enabling AOT/trim is a **deliberate non-goal**. The
trim/AOT analyzers + the CsWinRT AOT optimizer were **removed**, so reflection-based code (e.g.
`System.Reactive`, reflection-based `JsonSerializer`) builds **warning-free** — no `IL2026`/`IL3050`
enforcement. (To revive AOT readiness, restore `<IsAotCompatible>true` + the CsWinRT optimizer lines from
git history.)

The source-gen `JsonSerializerContext`s (`FinnhubJsonContext`, `FrankfurterJsonContext`,
`TwelveDataJsonContext`, `WatchlistJsonContext`) are kept as a **convention, not a build requirement** — new
JSON may use either source-gen or plain reflection.

⚠️ **Gotcha (while a context stays source-gen):** the JSON source generator does **not** support
`[JsonSerializable]` attributes split across multiple `partial` declarations of one context — it emits a
colliding hintName and the *whole* generator silently fails, cascading CS0534 onto **every** context in the
build. Keep all `[JsonSerializable]` for a given context on a **single** declaration (see
`ApiFinnhubQuoteDto.cs`).

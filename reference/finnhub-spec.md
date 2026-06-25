# Market Go Up — Command Palette Extension Spec

A PowerToys Command Palette extension that shows live-ish market quotes (stocks, crypto, FX) in the **Command Palette dock** as a pinnable band, plus a searchable add/remove flow inside the palette itself. Single data provider: **Finnhub** (free tier, user supplies their own API key).

> Subtitle / store description: *stocks · crypto · FX in your Command Palette*
> Command alias: `quotes` (or `mgu`)

---

## 0. Context for the implementer

- Target: **PowerToys Command Palette**, v0.100.0+ (the Dock + `GetDockBands` API shipped in 0.98/CmdPal 0.9, multi-monitor dock in 0.100.0).
- Language/stack: **C# / WinUI 3**, the standard CmdPal extension stack. Start from the official CmdPal extension project template (`dotnet new` template shipped in the PowerToys repo, or scaffold via Visual Studio).
- The dock band API is `ICommandProvider3::GetDockBands()`. **Before writing band code, read the actual API spec** in the PowerToys repo at `src/modules/cmdpal/doc/` and the CmdPal sample extensions under `src/modules/cmdpal/`. The band rendering capabilities (static button vs. dynamic text label) are the single load-bearing unknown — confirm from the sample code what a band can actually render and how its refresh/invoke works before committing to the inline-price UI.
- The dock is **always visible** (no auto-hide), so band content must be compact. Three pin regions exist: start / center / end.

### Build order (each step independently shippable)

1. Vanilla command provider loads in CmdPal (from template).
2. Implement `ICommandProvider3`, return ONE hardcoded band (`"BTC 64,000"`). Verify it renders + manual refresh works.
3. Wire Finnhub `/quote` behind that one band (hardcoded symbol).
4. Add/remove flow: search via `/search`, pin/unpin, persist.
5. Persistence (pinned items + API key in local app data).
6. Polish: detail page, color coding, error states.

---

## 1. Data provider — Finnhub

Base URL: `https://finnhub.io/api/v1`
Auth: append `&token={API_KEY}` to every request (or `X-Finnhub-Token` header).
Key: user-supplied, free tier. No credit card. Register at finnhub.io.

### Free-tier limits (design around these)
- **60 calls/minute**, plus a soft **~300 calls/day** ceiling.
- Manual refresh across a handful of pinned symbols stays far under both. Do NOT auto-poll aggressively.
- Real-time quotes solid for **US stocks**; international thinner.
- **Historical candles (`/stock/candle`) are PREMIUM** — return 403 on free keys. No sparklines/charts from Finnhub free. Band needs only `/quote`, so this only affects an optional detail view. Do not architect a chart dependency.

### Endpoint 1 — Quote (the workhorse; powers the band)

```
GET /api/v1/quote?symbol={SYMBOL}&token={KEY}
```

Response:
```json
{
  "c": 261.74,   // current price
  "d": 1.23,     // change (absolute)
  "dp": 0.47,    // percent change  <- drives the green/red arrow
  "h": 263.31,   // high of day
  "l": 260.68,   // low of day
  "o": 261.07,   // open
  "pc": 260.51,  // previous close
  "t": 1739808000 // unix timestamp
}
```

Notes:
- A bad/unknown symbol returns all-zero fields (`c: 0`, etc.), NOT an error. Treat all-zero as "no data / invalid symbol."
- Works for stocks, crypto, and FX — same response shape — as long as the symbol string is formatted per asset class (see §2).

### Endpoint 2 — Symbol search (powers the add flow)

```
GET /api/v1/search?q={QUERY}&token={KEY}
```

Response:
```json
{
  "count": 4,
  "result": [
    {
      "description": "APPLE INC",
      "displaySymbol": "AAPL",
      "symbol": "AAPL",
      "type": "Common Stock"
    }
  ]
}
```
Use `description` for the human label, `symbol` for the value to store + query.

### Endpoint 3 (optional) — Company profile (for detail view)

```
GET /api/v1/stock/profile2?symbol={SYMBOL}&token={KEY}
```
Returns `name`, `logo`, `marketCapitalization`, `exchange`, `finnhubIndustry`, `weburl`, `ticker`. Free. Nice-to-have for a click-through detail page. Stocks only.

### Symbol enumeration helpers (optional, for nicer add UX)
- Crypto symbols on an exchange: `GET /crypto/symbol?exchange=binance&token={KEY}`
- Forex pairs: `GET /forex/symbol?token={KEY}`

---

## 2. Asset classes & symbol formatting

The provider abstraction's only real job is formatting the symbol string per asset class, then calling `/quote`. Store the asset class alongside each pinned symbol.

| Asset class | `/quote` symbol format | Example | Notes |
|---|---|---|---|
| `Stock`  | bare ticker            | `AAPL`              | US tickers best supported |
| `Crypto` | `EXCHANGE:PAIR`        | `BINANCE:BTCUSDT`   | Binance is the safe default exchange |
| `Forex`  | `BROKER:PAIR`          | `OANDA:EUR_USD`     | OANDA is the safe default broker |

For search/add: Finnhub `/search` primarily returns equities. For crypto/FX, either (a) let the user pick asset class first then type the pair, or (b) preload pair lists from the enumeration helpers above and fuzzy-match locally. Simplest v1: an asset-class selector (Stock / Crypto / FX) in the add flow that determines the symbol prefix.

---

## 3. Provider interface

```csharp
public enum AssetClass { Stock, Crypto, Forex }

public record Quote(
    string Symbol,        // the stored symbol, e.g. "BINANCE:BTCUSDT"
    string DisplayLabel,  // what shows on the band, e.g. "BTC"
    decimal Price,        // Finnhub "c"
    decimal ChangePct,    // Finnhub "dp"
    bool   IsValid,       // false when Finnhub returned all-zeros
    DateTimeOffset FetchedAt
);

public record SymbolMatch(
    string Symbol,
    string Description,
    AssetClass AssetClass
);

public interface IQuoteProvider
{
    Task<Quote>            GetQuoteAsync(PinnedItem item, CancellationToken ct);
    Task<IReadOnlyList<SymbolMatch>> SearchAsync(string query, AssetClass assetClass, CancellationToken ct);
    bool   RequiresApiKey { get; }   // true for Finnhub
    string ProviderName   { get; }   // "Finnhub"
}
```

`FinnhubQuoteProvider` is the only concrete impl for v1. Keep the interface so a no-key fallback provider (CoinGecko/Stooq/Frankfurter) can be added later without touching the band layer.

### Symbol formatting helper
```csharp
static string ToFinnhubSymbol(string raw, AssetClass cls) => cls switch
{
    AssetClass.Stock  => raw.ToUpperInvariant(),
    AssetClass.Crypto => raw.Contains(':') ? raw : $"BINANCE:{raw.ToUpperInvariant()}",
    AssetClass.Forex  => raw.Contains(':') ? raw : $"OANDA:{raw.ToUpperInvariant()}",
    _ => raw
};
```

---

## 4. Persistence

Store in the extension's local app data (e.g. `%LOCALAPPDATA%\MarketGoUp\settings.json`). Single JSON file, last-write-wins.

```json
{
  "apiKey": "USER_FINNHUB_KEY",
  "refreshOnLoad": true,
  "pinned": [
    { "symbol": "AAPL",            "displayLabel": "AAPL", "assetClass": "Stock"  },
    { "symbol": "BINANCE:BTCUSDT", "displayLabel": "BTC",  "assetClass": "Crypto" },
    { "symbol": "OANDA:EUR_USD",   "displayLabel": "EUR/GBP", "assetClass": "Forex" }
  ]
}
```

```csharp
public record PinnedItem(string Symbol, string DisplayLabel, AssetClass AssetClass);
```

- API key in plaintext local config is the normal bar for this class of tool. Don't log it; don't put it in URLs that get logged.
- The Command Palette settings surface (`ISettings` / settings page in the CmdPal SDK) is the right place for the API-key input — use it rather than rolling your own.

---

## 5. UI surfaces

### A. Dock band (the headline feature)
- One band per pinned item, OR one band rendering all pinned items inline — **depends on what the band API allows** (resolve in build step 2).
- Content per item: `{DisplayLabel} {Price} {▲|▼}{ChangePct}%`, e.g. `AAPL 261.74 ▲0.47%`.
- Color: green when `ChangePct >= 0`, red when `< 0`. Use a neutral/grey state for `IsValid == false` (show `—` instead of a price).
- Click a band item → open the detail page (or the symbol on finnhub.io / a provider URL).
- Refresh: a manual "refresh" command/action. Optionally refresh-on-palette-open. No tight polling loop.

### B. Command Palette pages (the extension surface)
- **Top-level command** (`quotes`): opens a list page of currently pinned items with live values; each has context actions Refresh / Unpin / Move to dock region.
- **Add command**: asset-class selector → search box → results from `SearchAsync` with asset-class badges → selecting one appends to `pinned`, persists, refreshes bands.
- **Settings**: Finnhub API key field, refresh-on-load toggle.

### Display style decision (deferred)
Static-inline (all pinned shown at once) vs. rotating (cycle one at a time) was left open pending seeing the band render. Build static-inline first; it's simpler and matches the always-visible dock. Revisit rotation only if horizontal space is tight.

---

## 6. Error handling (Finnhub specifics)

- Always check HTTP status first. On non-2xx, attempt JSON parse but **fall back to raw text** — Finnhub error bodies are not always clean JSON.
- `429 Too Many Requests` → exponential backoff; surface a quiet "rate limited, retry shortly" state, don't crash the band.
- `403` on any endpoint → premium-gated (e.g. candles). Surface clearly; don't retry.
- `401` → invalid/missing API key → prompt user to set it in settings.
- All-zero `/quote` response → treat as invalid symbol, show `—`, optionally flag in the pinned list.
- Crypto/FX candle calls (if ever used) return `{"s":"no_data"}` for empty ranges — handle as empty, not error.

---

## 7. Out of scope for v1 (note, don't build)
- Sparklines / charts (Finnhub historical candles are premium).
- WebSocket streaming (free tier allows 50 symbols, but manual-refresh REST is simpler and sufficient; revisit later).
- No-key fallback providers (CoinGecko / Stooq / Frankfurter) — interface leaves room, implement later.
- Portfolio quantities / cost basis / P&L.

---

## 8. Naming / packaging
- Extension display name: **Market Go Up**
- Subtitle: *stocks · crypto · FX in your Command Palette*
- Store/WinGet tags (these do the discovery work): `market`, `stocks`, `crypto`, `forex`, `ticker`, `quotes`, `watchlist`, `finance`, `dock`
- Command alias: `quotes`

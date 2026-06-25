using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// Live market data from Twelve Data (https://twelvedata.com). One IMarketDataProvider source covering
// stocks, crypto AND forex from a single API — it formats each neutral DomainInstrument into Twelve
// Data's symbol syntax, calls /quote (batched), /time_series and /symbol_search, and maps the raw
// Api* DTOs into DomainQuote / DomainCandleSeries / DomainInstrument. Nothing outside this class
// references Twelve Data, so it slots in beside Finnhub/Frankfurter with no repository or UI changes.
//
// Why it's the primary provider when configured: unlike Finnhub, Twelve Data's /time_series (candle)
// endpoint is on the FREE tier, so a free key renders real symbol-detail charts. Registered FIRST in
// MarketExtensionCommandsProvider; Supports() is gated on having a key (below), so with no key set the
// repository's first-match routing falls through to Finnhub (stocks/crypto) / Frankfurter (FX) — no
// regression for users without a Twelve Data key. Set a key and Twelve Data transparently takes over.
//
// Rate limit: the free tier is ~8 credits/minute and ~800/day (one credit per symbol). That tight
// per-minute cap is why GetQuotesAsync batches every instrument into a SINGLE /quote call rather than
// fanning out one request per symbol (which a sizable watchlist would blow on each refresh).
internal sealed class TwelveDataMarketDataProvider : IMarketDataProvider
{
    // The Twelve Data key, sourced exclusively from the extension's settings (no built-in/baked key).
    // Read from MarketSettingsManager on each access so a key change applies on the next request
    // without a reload; empty until the user sets one.
    private static string ApiKey => MarketSettingsManager.Instance.TwelveDataApiKey;
    private static bool HasApiKey => MarketSettingsManager.Instance.HasTwelveDataApiKey;

    // Reuse a single client (creating one per request exhausts sockets). The trailing slash matters:
    // relative request paths resolve under the host root. AOT-safe.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.twelvedata.com/") };

    // Max search results surfaced (also the /symbol_search outputsize). Keeps the list usable.
    private const int MaxSearchResults = 25;

    // Twelve Data covers all three asset classes — but only claim them when a key is configured, so the
    // repository's first-match routing falls through to Finnhub/Frankfurter when no key is set.
    public bool Supports(AssetCategory category) =>
        HasApiKey && category is AssetCategory.Stock or AssetCategory.Crypto or AssetCategory.Currency;

    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        if (instruments.Count == 0)
            return [];

        // No key configured (settings is the only source) — return everything as unavailable rather
        // than firing keyless /quote calls that would just error.
        if (!HasApiKey)
        {
            Log.Warn("TwelveData", "no API key set — skipping quotes (set one in extension settings)");
            return instruments.Select(Invalid).ToArray();
        }

        // Map each instrument to its Twelve Data symbol, deduped (two instruments shouldn't collide on
        // a TD symbol in practice). The response is keyed by these symbols, so we match back by them.
        var tdSymbols = new HashSet<string>(instruments.Select(ToTwelveDataSymbol), StringComparer.OrdinalIgnoreCase);

        try
        {
            // Batch every symbol into ONE /quote call (comma-joined). One credit per symbol either way,
            // but a single request stays under the tight 8-requests/minute free cap. The comma separates
            // values; the "/" inside crypto/FX symbols is escaped per value.
            var joined = string.Join(",", tdSymbols.Select(Uri.EscapeDataString));
            // NEVER log the full URL — it carries the API key. Log the symbols only.
            Log.Info("TwelveData", $"GET /quote symbols={string.Join(",", tdSymbols)}");
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync($"quote?symbol={joined}&apikey={ApiKey}", c), "TwelveData", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("TwelveData", $"quote <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                // e.g. 429 rate-limited, 401 bad key. Degrade to all-unavailable rather than throwing.
                Log.Warn("TwelveData", $"quote: non-success status {(int)response.StatusCode} — treating as unavailable");
                return instruments.Select(Invalid).ToArray();
            }

            var quotes = ParseQuotes(json, tdSymbols);

            return instruments.Select(i =>
            {
                var tdSymbol = ToTwelveDataSymbol(i);
                if (quotes.TryGetValue(tdSymbol, out var dto) && dto.Close is > 0m &&
                    !string.Equals(dto.Status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var (currency, price, change) = ResolveCurrency(i, dto.Close.Value, dto.Change ?? 0m, dto.Currency);
                    var quote = new DomainQuote(
                        i.Symbol, i.Name, i.Category,
                        price, change, dto.PercentChange ?? 0m, IsValid: true, Currency: currency);
                    Log.Info("TwelveData", $"{tdSymbol} -> price={quote.Price} change%={quote.ChangePercent} ccy={currency}");
                    return quote;
                }

                Log.Warn("TwelveData", $"{tdSymbol}: no valid quote in response — treating as invalid");
                return Invalid(i);
            }).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network / HTTP failure: degrade to invalid quotes rather than breaking the whole page.
            Log.Error("TwelveData", "quote request failed", ex);
            return instruments.Select(Invalid).ToArray();
        }
    }

    // Normalize Twelve Data's /quote response into a symbol -> DTO map. The shape depends on count:
    // a single symbol returns a bare quote object; multiple symbols return an object keyed by symbol.
    private static Dictionary<string, ApiTwelveDataQuoteDto> ParseQuotes(
        string json, HashSet<string> requestedSymbols)
    {
        var result = new Dictionary<string, ApiTwelveDataQuoteDto>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (requestedSymbols.Count == 1)
            {
                var dto = JsonSerializer.Deserialize(json, TwelveDataJsonContext.Default.ApiTwelveDataQuoteDto);
                if (dto is not null)
                {
                    ReportIfRateLimited(dto); // a single-symbol error object parses straight into the DTO
                    result[requestedSymbols.First()] = dto;
                }
            }
            else
            {
                var map = JsonSerializer.Deserialize(
                    json, TwelveDataJsonContext.Default.DictionaryStringApiTwelveDataQuoteDto);
                if (map is not null)
                    foreach (var kv in map)
                        result[kv.Key] = kv.Value;
            }
        }
        catch (JsonException ex)
        {
            // A global error (bad key, rate limit) can come back as a bare {"code":…,"status":"error"}
            // object even on HTTP 200, which won't shape-match the multi-symbol map. Surface a 429 to the
            // banner, then degrade to "no quotes" (all invalid) rather than throwing — keeps it out of the
            // error/Sentry path.
            ReportIfGlobalRateLimit(json);
            Log.Warn("TwelveData", $"quote: could not parse response ({ex.Message}) — treating as unavailable");
        }
        return result;
    }

    // Twelve Data signals throttling with code 429 — even inside a 200-OK body — so flip the rate-limit
    // banner when we see it (HttpRetry only catches a real HTTP 429).
    private static void ReportIfRateLimited(ApiTwelveDataQuoteDto dto)
    {
        if (dto.Code == 429)
            RateLimitSignal.Instance.ReportRateLimited();
    }

    // The multi-symbol path's global-error case: the bare error object failed to deserialize as the keyed
    // map, so re-parse it as a single quote DTO purely to read its code (and ignore if it isn't one).
    private static void ReportIfGlobalRateLimit(string json)
    {
        try
        {
            var error = JsonSerializer.Deserialize(json, TwelveDataJsonContext.Default.ApiTwelveDataQuoteDto);
            if (error?.Code == 429)
                RateLimitSignal.Instance.ReportRateLimited();
        }
        catch (JsonException) { /* not a recognizable error object — ignore */ }
    }

    public async Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return [];

        if (!HasApiKey)
        {
            Log.Warn("TwelveData", "no API key set — skipping search (set one in extension settings)");
            return [];
        }

        try
        {
            // NEVER log the full URL — it carries the API key. Log the query only.
            Log.Info("TwelveData", $"GET /symbol_search q={query}");
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync(
                    $"symbol_search?symbol={Uri.EscapeDataString(query)}&outputsize={MaxSearchResults}&apikey={ApiKey}", c),
                "TwelveData", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("TwelveData", $"search '{query}' <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("TwelveData", $"search '{query}': non-success status {(int)response.StatusCode} — no results");
                return [];
            }

            var dto = JsonSerializer.Deserialize(json, TwelveDataJsonContext.Default.ApiTwelveDataSearchDto);
            if (dto?.Data is not { Count: > 0 } rows)
                return [];

            // Map to neutral identities: derive the asset class from instrument_type, normalize the
            // symbol back to our convention so it round-trips through ToTwelveDataSymbol (and matches the
            // icon resolver + watchlist storage), dedupe by symbol, and cap for the UI.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instruments = new List<DomainInstrument>();
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Symbol))
                    continue;

                var category = ToCategory(r.InstrumentType);
                var symbol = NormalizeSymbol(r.Symbol!, category);
                // FX results must be a clean 6-letter pair to split downstream; skip bare currencies.
                if (string.IsNullOrEmpty(symbol) ||
                    (category == AssetCategory.Currency && symbol.Length != 6) ||
                    !seen.Add(symbol))
                    continue;

                var name = string.IsNullOrWhiteSpace(r.InstrumentName) ? symbol : r.InstrumentName!;
                instruments.Add(new DomainInstrument(symbol, name, category));
                if (instruments.Count >= MaxSearchResults)
                    break;
            }

            Log.Info("TwelveData", $"search '{query}' -> {instruments.Count} result(s)");
            return instruments;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("TwelveData", $"search '{query}': request failed", ex);
            return [];
        }
    }

    public async Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        if (!HasApiKey)
        {
            Log.Warn("TwelveData", "no API key set — skipping candles (set one in extension settings)");
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }

        var symbol = ToTwelveDataSymbol(instrument);
        var interval = ToTwelveDataInterval(range.Interval());
        var outputsize = OutputSize(range);

        try
        {
            // NEVER log the full URL — it carries the API key. Log symbol/interval/range only.
            Log.Info("TwelveData", $"GET /time_series symbol={symbol} interval={interval} range={range}");
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync(
                    $"time_series?symbol={Uri.EscapeDataString(symbol)}&interval={interval}" +
                    $"&outputsize={outputsize}&apikey={ApiKey}", c),
                "TwelveData", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("TwelveData", $"{symbol} candles <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("TwelveData", $"{symbol} candles: non-success status {(int)response.StatusCode} — no chart data");
                return DomainCandleSeries.Invalid(instrument.Symbol, range);
            }

            var dto = JsonSerializer.Deserialize(json, TwelveDataJsonContext.Default.ApiTwelveDataTimeSeriesDto);
            if (dto is not { Status: "ok" } || dto.Values is not { Count: > 0 } values)
            {
                Log.Warn("TwelveData", $"{symbol} candles: status={dto?.Status ?? "null"} — no chart data");
                return DomainCandleSeries.Invalid(instrument.Symbol, range);
            }

            // Twelve Data returns values newest-first; the chart (and the header's last-vs-first change)
            // needs them oldest-first. Walk in reverse, dropping any point missing a close or datetime.
            var points = new List<CandlePoint>(values.Count);
            for (var i = values.Count - 1; i >= 0; i--)
            {
                var v = values[i];
                if (v.Close is { } close &&
                    DateTimeOffset.TryParse(
                        v.Datetime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                    points.Add(new CandlePoint(time, close));
            }

            if (points.Count == 0)
            {
                Log.Warn("TwelveData", $"{symbol} candles: no usable points — no chart data");
                return DomainCandleSeries.Invalid(instrument.Symbol, range);
            }

            Log.Info("TwelveData", $"{symbol} candles -> {points.Count} pts");
            return new DomainCandleSeries(instrument.Symbol, range, points, IsValid: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("TwelveData", $"{symbol} candles: request failed", ex);
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }
    }

    private static DomainQuote Invalid(DomainInstrument i) =>
        new(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false);

    // Resolve the native currency (and major-unit price/change) for a quote. Per category:
    //   * Currency — the pair's quote currency (EURUSD → USD), never the `currency` field.
    //   * Crypto   — always USD; we price against USD (BTC/USD), and TD's `currency` adds nothing.
    //   * Stock    — the /quote `currency` field, normalized (handles London's GBp/GBX pence → GBP, ÷100).
    private static (string Currency, decimal Price, decimal Change) ResolveCurrency(
        DomainInstrument instrument, decimal price, decimal change, string? rawCurrency) =>
        instrument.Category switch
        {
            AssetCategory.Currency => (CurrencyHelper.QuoteCurrencyOfPair(instrument.Symbol), price, change),
            AssetCategory.Crypto => ("USD", price, change),
            _ => CurrencyHelper.NormalizeStockQuote(rawCurrency, price, change),
        };

    // Twelve Data's instrument_type -> our neutral asset class. Equity-ish types (Common Stock, ETF,
    // Index, ADR, ...) all price as stocks; only crypto and forex map elsewhere.
    private static AssetCategory ToCategory(string? instrumentType) => instrumentType?.ToLowerInvariant() switch
    {
        "digital currency" or "cryptocurrency" or "crypto" => AssetCategory.Crypto,
        "physical currency" or "forex" or "currency" => AssetCategory.Currency,
        _ => AssetCategory.Stock,
    };

    // Provider symbol -> our neutral DomainInstrument.Symbol, so search results round-trip through
    // ToTwelveDataSymbol and match the icon resolver + watchlist storage:
    //   Stock     AAPL    -> AAPL
    //   Crypto    BTC/USD -> BTC      (our crypto symbol is the bare base coin)
    //   Currency  EUR/USD -> EURUSD   (6-letter pair, matching Frankfurter's convention)
    private static string NormalizeSymbol(string providerSymbol, AssetCategory category)
    {
        var s = providerSymbol.Trim().ToUpperInvariant();
        return category switch
        {
            AssetCategory.Crypto => s.Contains('/') ? s[..s.IndexOf('/')] : s,
            AssetCategory.Currency => s.Replace("/", string.Empty),
            _ => s,
        };
    }

    // Neutral ticker -> Twelve Data symbol syntax. The mirror of NormalizeSymbol; keeps the catalog
    // and watchlist provider-agnostic.
    //   Stock     AAPL    -> AAPL
    //   Crypto    BTC     -> BTC/USD
    //   Currency  EURUSD  -> EUR/USD
    private static string ToTwelveDataSymbol(DomainInstrument instrument)
    {
        var s = instrument.Symbol.ToUpperInvariant();
        return instrument.Category switch
        {
            AssetCategory.Stock => s,
            AssetCategory.Crypto => $"{s}/USD",
            AssetCategory.Currency => s.Length == 6 ? $"{s[..3]}/{s[3..]}" : s,
            _ => s,
        };
    }

    // Neutral CandleInterval -> Twelve Data's interval token. The candle analog of ToTwelveDataSymbol —
    // the ONLY place these provider-specific tokens live (cf. Finnhub's 5/30/60/D/W).
    private static string ToTwelveDataInterval(CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMin   => "5min",
        CandleInterval.ThirtyMin => "30min",
        CandleInterval.Hourly    => "1h",
        CandleInterval.Daily     => "1day",
        CandleInterval.Weekly    => "1week",
        _ => "1day",
    };

    // Bars to request per range. Twelve Data returns the most-recent N at the given interval, so this
    // sizes each window; all are well under TD's 5000 max.
    private static int OutputSize(ChartRange range) => range switch
    {
        ChartRange.OneDay   => 78,   // ~1 trading day of 5-min bars
        ChartRange.OneWeek  => 70,   // ~1 week of 30-min bars
        ChartRange.OneMonth => 160,  // ~1 month of hourly bars
        ChartRange.OneYear  => 365,  // ~1 year of daily bars
        ChartRange.FiveYear => 260,  // ~5 years of weekly bars
        _ => 100,
    };
}

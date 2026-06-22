using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// Live market data from Finnhub (https://finnhub.io). One IMarketDataProvider source: it formats
// each neutral DomainInstrument into Finnhub's symbol syntax, calls /quote, and maps the raw
// ApiFinnhubQuoteDto into a DomainQuote. Nothing outside this class references Finnhub, so a
// different provider can replace it without touching the repository or the UI.
internal sealed class FinnhubMarketDataProvider : IMarketDataProvider
{
    // Loaded from the gitignored secrets.props at build time (see MarketExtension.csproj's
    // GenerateSecrets target + secrets.props.template). TODO: move to per-user settings / BYO-key.
    private const string ApiKey = Secrets.FinnhubApiKey;

    // Reuse a single client (creating one per request exhausts sockets). AOT-safe.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://finnhub.io/api/v1/") };

    // Finnhub's free tier serves stocks and crypto; forex (OANDA:*) is premium-gated.
    public bool Supports(AssetCategory category) => category is AssetCategory.Stock or AssetCategory.Crypto;

    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        // One /quote call per instrument, fanned out. A handful of symbols stays well under the
        // free-tier 60 calls/minute limit.
        return await Task.WhenAll(instruments.Select(i => FetchQuoteAsync(i, ct))).ConfigureAwait(false);
    }

    // Max search results surfaced. Finnhub can return dozens for a common name; we cap to keep the
    // list usable (one /search call regardless — the cap is purely a UI trim).
    private const int MaxSearchResults = 25;

    public async Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
            return [];

        try
        {
            // exchange=US keeps results to plain US tickers whose canonical `symbol` round-trips
            // through ToFinnhubSymbol(Stock) for the later /quote (see InstrumentCatalog note). We
            // therefore treat every match as a Stock; crypto/FX search would need symbol-format
            // reconciliation and is deferred.
            // NEVER log the full URL — it carries the API token. Log the query only.
            Log.Info("Finnhub", $"GET /search q={query}");
            using var response = await Http.GetAsync(
                $"search?q={Uri.EscapeDataString(query)}&exchange=US&token={ApiKey}", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Finnhub", $"search '{query}' <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                // e.g. 429 rate-limited. Degrade to no results rather than throwing.
                Log.Warn("Finnhub", $"search '{query}': non-success status {(int)response.StatusCode} — no results");
                return [];
            }

            var dto = JsonSerializer.Deserialize(json, FinnhubJsonContext.Default.ApiFinnhubSearchDto);
            if (dto?.Result is not { Count: > 0 } results)
                return [];

            // Map to neutral identities, dropping entries without a usable symbol, deduping by
            // symbol (a name can list across exchanges), and capping for the UI.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instruments = new List<DomainInstrument>();
            foreach (var r in results)
            {
                var symbol = r.Symbol;
                if (string.IsNullOrWhiteSpace(symbol) || !seen.Add(symbol))
                    continue;

                var name = string.IsNullOrWhiteSpace(r.Description) ? symbol : r.Description!;
                instruments.Add(new DomainInstrument(symbol, name, AssetCategory.Stock));
                if (instruments.Count >= MaxSearchResults)
                    break;
            }

            Log.Info("Finnhub", $"search '{query}' -> {instruments.Count} result(s)");
            return instruments;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Finnhub", $"search '{query}': request failed", ex);
            return [];
        }
    }

    private static async Task<DomainQuote> FetchQuoteAsync(DomainInstrument instrument, CancellationToken ct)
    {
        var symbol = ToFinnhubSymbol(instrument);
        try
        {
            // NEVER log the full URL — it carries the API token. Log the symbol only.
            Log.Info("Finnhub", $"GET /quote symbol={symbol}");
            using var response = await Http.GetAsync(
                $"quote?symbol={Uri.EscapeDataString(symbol)}&token={ApiKey}", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Finnhub", $"{symbol} <- {(int)response.StatusCode} {response.StatusCode}  body={json}");

            if (!response.IsSuccessStatusCode)
            {
                // e.g. 403 premium-gated (forex), 429 rate-limited.
                Log.Warn("Finnhub", $"{symbol}: non-success status {(int)response.StatusCode} — treating as unavailable");
                return Invalid(instrument);
            }

            var dto = JsonSerializer.Deserialize(json, FinnhubJsonContext.Default.ApiFinnhubQuoteDto);

            // Finnhub returns all-zero fields for unknown/unavailable symbols (not an HTTP error).
            var price = dto?.Current ?? 0m;
            if (price == 0m)
            {
                Log.Warn("Finnhub", $"{symbol}: all-zero quote — treating as invalid symbol");
                return Invalid(instrument);
            }

            var quote = new DomainQuote(
                instrument.Symbol, instrument.Name, instrument.Category,
                price, dto!.Change ?? 0m, dto.ChangePercent ?? 0m, IsValid: true);
            Log.Info("Finnhub", $"{symbol} -> price={quote.Price} change%={quote.ChangePercent}");
            return quote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network / HTTP / parse failure: degrade to an invalid quote rather than breaking the
            // whole page. Richer error/rate-limit UX is deferred.
            Log.Error("Finnhub", $"{symbol}: request failed", ex);
            return Invalid(instrument);
        }
    }

    public async Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        var symbol = ToFinnhubSymbol(instrument);
        // Crypto candles come from the parallel /crypto/candle endpoint (same args + response shape).
        var endpoint = instrument.Category == AssetCategory.Crypto ? "crypto/candle" : "stock/candle";
        var resolution = ToFinnhubResolution(range.Interval());
        var to = DateTimeOffset.UtcNow;
        var fromUnix = (to - range.Lookback()).ToUnixTimeSeconds();
        var toUnix = to.ToUnixTimeSeconds();

        try
        {
            // NEVER log the full URL — it carries the API token. Log symbol/resolution/range only.
            Log.Info("Finnhub", $"GET /{endpoint} symbol={symbol} resolution={resolution} range={range}");
            using var response = await Http.GetAsync(
                $"{endpoint}?symbol={Uri.EscapeDataString(symbol)}&resolution={resolution}" +
                $"&from={fromUnix}&to={toUnix}&token={ApiKey}", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Finnhub", $"{symbol} candles <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                // 403 = premium-gated (candles are a paid endpoint), 429 = rate-limited, ...
                Log.Warn("Finnhub", $"{symbol} candles: non-success status {(int)response.StatusCode} — no chart data");
                return DomainCandleSeries.Invalid(instrument.Symbol, range);
            }

            var dto = JsonSerializer.Deserialize(json, FinnhubJsonContext.Default.ApiFinnhubCandleDto);
            if (dto is not { Status: "ok" } ||
                dto.Close is not { Count: > 0 } closes ||
                dto.Timestamps is not { Count: > 0 } times)
            {
                Log.Warn("Finnhub", $"{symbol} candles: status={dto?.Status ?? "null"} — no chart data");
                return DomainCandleSeries.Invalid(instrument.Symbol, range);
            }

            // The c/t arrays are parallel by index; guard against a ragged response.
            var count = Math.Min(closes.Count, times.Count);
            var points = new List<CandlePoint>(count);
            for (var i = 0; i < count; i++)
                points.Add(new CandlePoint(DateTimeOffset.FromUnixTimeSeconds(times[i]), closes[i]));

            Log.Info("Finnhub", $"{symbol} candles -> {points.Count} pts");
            return new DomainCandleSeries(instrument.Symbol, range, points, IsValid: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Finnhub", $"{symbol} candles: request failed", ex);
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }
    }

    private static DomainQuote Invalid(DomainInstrument i) =>
        new(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false);

    // Neutral CandleInterval -> Finnhub's resolution token. The candle analog of ToFinnhubSymbol —
    // the ONLY place these provider-specific tokens live. A different provider maps the same
    // CandleInterval to its own vocabulary (e.g. Twelve Data: 5min/30min/1h/1day/1week).
    private static string ToFinnhubResolution(CandleInterval interval) => interval switch
    {
        CandleInterval.FiveMin   => "5",
        CandleInterval.ThirtyMin => "30",
        CandleInterval.Hourly    => "60",
        CandleInterval.Daily     => "D",
        CandleInterval.Weekly    => "W",
        _ => "D",
    };

    // Neutral ticker -> Finnhub symbol syntax. Keeps InstrumentCatalog provider-agnostic.
    //   Stock    AAPL    -> AAPL
    //   Crypto   BTC     -> BINANCE:BTCUSDT
    //   Currency EURUSD  -> OANDA:EUR_USD   (premium on the free tier; see InstrumentCatalog)
    private static string ToFinnhubSymbol(DomainInstrument instrument) => instrument.Category switch
    {
        AssetCategory.Stock    => instrument.Symbol.ToUpperInvariant(),
        AssetCategory.Crypto   => $"BINANCE:{instrument.Symbol.ToUpperInvariant()}USDT",
        AssetCategory.Currency => $"OANDA:{instrument.Symbol.ToUpperInvariant().Insert(3, "_")}",
        _ => instrument.Symbol,
    };
}

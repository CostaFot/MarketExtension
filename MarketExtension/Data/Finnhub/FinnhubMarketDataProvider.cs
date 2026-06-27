using System;
using System.Collections.Concurrent;
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
    // The Finnhub key, sourced exclusively from the extension's settings (no built-in/baked key).
    // Read from MarketSettingsManager on each access so a key change applies on the next request
    // without a reload; empty until the user sets one.
    private static string ApiKey => MarketSettingsManager.Instance.FinnhubApiKey;
    private static bool HasApiKey => MarketSettingsManager.Instance.HasFinnhubApiKey;

    // Reuse a single client (creating one per request exhausts sockets). AOT-safe.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://finnhub.io/api/v1/") };

    // symbol → raw native-currency code (e.g. "USD", "GBp"), resolved once via /stock/profile2 and cached
    // for the process lifetime. Currency is static metadata, so this is one extra call per NEW stock symbol
    // (trivial against the ~300/day budget) — and most are "USD". Only successful resolutions are cached, so
    // a transient profile2 failure retries on the next poll rather than pinning the wrong currency.
    private static readonly ConcurrentDictionary<string, string> StockCurrencyCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Finnhub's free tier serves stocks and crypto; forex (OANDA:*) is premium-gated.
    public bool Supports(AssetCategory category) => category is AssetCategory.Stock or AssetCategory.Crypto;

    // Finnhub is the (only) news source — it serves the /news market-news feed (GetNewsAsync). This opts
    // the provider in past the interface's SupportsNews gate, which defaults false for every other source.
    public bool SupportsNews => true;

    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        // No key configured (settings is the only source) — return everything as unavailable rather
        // than firing keyless /quote calls that would just 401.
        if (!HasApiKey)
        {
            Log.Warn("Finnhub", "no API key set — skipping quotes (set one in extension settings)");
            return instruments.Select(Invalid).ToArray();
        }

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

        if (!HasApiKey)
        {
            Log.Warn("Finnhub", "no API key set — skipping search (set one in extension settings)");
            return [];
        }

        try
        {
            // exchange=US keeps results to plain US tickers whose canonical `symbol` round-trips
            // through ToFinnhubSymbol(Stock) for the later /quote (see InstrumentCatalog note). We
            // therefore treat every match as a Stock; crypto/FX search would need symbol-format
            // reconciliation and is deferred.
            // NEVER log the full URL — it carries the API token. Log the query only.
            Log.Info("Finnhub", $"GET /search q={query}");
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync($"search?q={Uri.EscapeDataString(query)}&exchange=US&token={ApiKey}", c),
                "Finnhub", ct).ConfigureAwait(false);

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
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync($"quote?symbol={Uri.EscapeDataString(symbol)}&token={ApiKey}", c),
                "Finnhub", ct).ConfigureAwait(false);

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

            // Finnhub's /quote carries no currency — resolve the native currency (and fold any pence quote
            // to major units) before building the domain quote. Crypto is USD-quoted; stocks look up
            // /stock/profile2 (cached). This is the only place we await a second call, and only for stocks.
            var (currency, normPrice, normChange) =
                await ResolveCurrencyAsync(instrument, price, dto!.Change ?? 0m, ct).ConfigureAwait(false);
            var quote = new DomainQuote(
                instrument.Symbol, instrument.Name, instrument.Category,
                normPrice, normChange, dto.ChangePercent ?? 0m, IsValid: true, Currency: currency);
            Log.Info("Finnhub", $"{symbol} -> price={quote.Price} change%={quote.ChangePercent} ccy={currency}");
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
        if (!HasApiKey)
        {
            Log.Warn("Finnhub", "no API key set — skipping candles (set one in extension settings)");
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }

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
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync(
                    $"{endpoint}?symbol={Uri.EscapeDataString(symbol)}&resolution={resolution}" +
                    $"&from={fromUnix}&to={toUnix}&token={ApiKey}", c),
                "Finnhub", ct).ConfigureAwait(false);

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

    // Latest market news for a category (Finnhub GET /news), the IMarketDataProvider.GetNewsAsync impl
    // (gated by SupportsNews => true above). minId returns only items AFTER that news id (0 = the latest
    // batch); pass the largest Id from a prior call to page forward without re-fetching. Returns
    // DomainNews items in Finnhub's order (newest first). A no-key install or any failure (network /
    // non-2xx / parse) degrades to an empty list rather than throwing.
    public async Task<IReadOnlyList<DomainNews>> GetNewsAsync(
        NewsCategory category, long minId = 0, CancellationToken ct = default)
    {
        if (!HasApiKey)
        {
            Log.Warn("Finnhub", "no API key set — skipping news (set one in extension settings)");
            return [];
        }

        var categoryToken = ToFinnhubNewsCategory(category);
        try
        {
            // NEVER log the full URL — it carries the API token. Log the category/minId only.
            Log.Info("Finnhub", $"GET /news category={categoryToken} minId={minId}");
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync($"news?category={Uri.EscapeDataString(categoryToken)}&minId={minId}&token={ApiKey}", c),
                "Finnhub", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Finnhub", $"news {categoryToken} <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                // e.g. 429 rate-limited. Degrade to no news rather than throwing.
                Log.Warn("Finnhub", $"news {categoryToken}: non-success status {(int)response.StatusCode} — no news");
                return [];
            }

            // /news returns a bare top-level JSON array — deserialize to ApiFinnhubNewsDto[].
            var dtos = JsonSerializer.Deserialize(json, FinnhubJsonContext.Default.ApiFinnhubNewsDtoArray);
            if (dtos is not { Length: > 0 })
                return [];

            // Drop items missing the essentials (id / headline / url): without them a row can't render or
            // open. The survivors map directly (see ToDomainNews).
            var news = dtos
                .Where(d => d.Id is not null
                    && !string.IsNullOrWhiteSpace(d.Headline)
                    && !string.IsNullOrWhiteSpace(d.Url))
                .Select(ToDomainNews)
                .ToList();

            Log.Info("Finnhub", $"news {categoryToken} -> {news.Count} item(s)");
            return news;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Finnhub", $"news {categoryToken}: request failed", ex);
            return [];
        }
    }

    // ApiFinnhubNewsDto -> DomainNews. Only called for items already filtered to carry id/headline/url, so
    // those three map directly (non-null asserted); the rest coalesce to empty/null. datetime is UNIX seconds.
    private static DomainNews ToDomainNews(ApiFinnhubNewsDto dto) => new(
        Id: dto.Id!.Value,
        Headline: dto.Headline!,
        Source: dto.Source ?? string.Empty,
        Summary: dto.Summary ?? string.Empty,
        Category: dto.Category ?? string.Empty,
        ArticleUrl: dto.Url!,
        Published: DateTimeOffset.FromUnixTimeSeconds(dto.Datetime ?? 0),
        ImageUrl: string.IsNullOrWhiteSpace(dto.Image) ? null : dto.Image,
        Related: string.IsNullOrWhiteSpace(dto.Related) ? null : dto.Related);

    private static DomainQuote Invalid(DomainInstrument i) =>
        new(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false);

    // Resolve the native currency (and major-unit price/change) for a Finnhub quote. Crypto is USD (we
    // price against USD and Finnhub serves no FX here); a stock looks up its reporting currency via
    // /stock/profile2 and normalizes any pence quote (London GBp/GBX → GBP, ÷100).
    private static async Task<(string Currency, decimal Price, decimal Change)> ResolveCurrencyAsync(
        DomainInstrument instrument, decimal price, decimal change, CancellationToken ct)
    {
        if (instrument.Category != AssetCategory.Stock)
            return ("USD", price, change);

        var raw = await GetStockCurrencyCodeAsync(instrument.Symbol, ct).ConfigureAwait(false);
        return CurrencyHelper.NormalizeStockQuote(raw, price, change);
    }

    // The raw reporting-currency code for a stock symbol, cached forever. A US-only (free) key returns
    // "USD" for everything anyway; only a paid key's non-US listings make this matter. profile2 is on the
    // free tier and reliable, so we cache EVERY result — including a USD fallback when it's unavailable — to
    // bound this to exactly one extra call per symbol per session (doubling Finnhub's poll volume by
    // retrying a flaky profile2 every tick would be worse than a rare, reload-healed USD mislabel).
    private static async Task<string> GetStockCurrencyCodeAsync(string symbol, CancellationToken ct)
    {
        var key = symbol.ToUpperInvariant();
        if (StockCurrencyCache.TryGetValue(key, out var cached))
            return cached;

        var resolved = await FetchProfileCurrencyAsync(key, ct).ConfigureAwait(false) ?? "USD";
        StockCurrencyCache[key] = resolved;
        return resolved;
    }

    private static async Task<string?> FetchProfileCurrencyAsync(string symbol, CancellationToken ct)
    {
        try
        {
            // NEVER log the full URL — it carries the API token. Log the symbol only.
            Log.Info("Finnhub", $"GET /stock/profile2 symbol={symbol}");
            using var response = await HttpRetry.SendAsync(
                c => Http.GetAsync($"stock/profile2?symbol={Uri.EscapeDataString(symbol)}&token={ApiKey}", c),
                "Finnhub", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Finnhub", $"profile2 {symbol}: non-success status {(int)response.StatusCode} — defaulting USD");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize(json, FinnhubJsonContext.Default.ApiFinnhubProfileDto);
            return string.IsNullOrWhiteSpace(dto?.Currency) ? null : dto!.Currency;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Finnhub", $"profile2 {symbol}: request failed", ex);
            return null;
        }
    }

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

    // Neutral NewsCategory -> Finnhub's /news category token. The news analog of ToFinnhubSymbol — the
    // ONLY place this provider-specific token lives. A different news source maps the same NewsCategory
    // to its own vocabulary.
    private static string ToFinnhubNewsCategory(NewsCategory category) => category switch
    {
        NewsCategory.General => "general",
        NewsCategory.Forex   => "forex",
        NewsCategory.Crypto  => "crypto",
        NewsCategory.Merger  => "merger",
        _ => "general",
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

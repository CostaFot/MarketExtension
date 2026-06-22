using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// Live foreign-exchange rates from Frankfurter (https://frankfurter.dev) — a free, keyless wrapper
// over the European Central Bank's daily reference rates. One IMarketDataProvider source: it splits a
// neutral 6-letter pair (EURUSD) into base/quote currencies, calls Frankfurter's time-series endpoint,
// and maps the response into DomainQuotes / DomainCandleSeries. Nothing outside this class references
// Frankfurter, so it slots in beside FinnhubMarketDataProvider with no repository or UI changes — it's
// the keyless FX provider the seam was designed for (see CLAUDE.md "To add a provider").
//
// ECB publishes one fixing per business day, so FX here is DAILY ONLY:
//   • a quote's "change" is day-over-day (latest vs previous fixing — the FX analog of Finnhub's `d`);
//   • charts plot daily closes, so the intraday 1D/1W tabs show the most recent daily fixings, not bars.
// There's no API key and no documented rate limit, so (unlike Finnhub) there's no key short-circuit.
internal sealed class FrankfurterMarketDataProvider : IMarketDataProvider
{
    // Reuse a single client (creating one per request exhausts sockets). The trailing slash matters:
    // relative request paths resolve under /v1/. AOT-safe.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.frankfurter.dev/v1/") };

    public bool Supports(AssetCategory category) => category is AssetCategory.Currency;

    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default) =>
        await Task.WhenAll(instruments.Select(i => FetchQuoteAsync(i, ct))).ConfigureAwait(false);

    // Frankfurter has no free-text symbol endpoint, so "search" is a local filter over the catalog's
    // FX pairs (identity only, no network call) — enough to make the pairs findable from Markets Search
    // for installs whose watchlist predates them. (Crypto/Finnhub-side FX search is still deferred.)
    public Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        IReadOnlyList<DomainInstrument> matches = query.Length == 0
            ? []
            : [.. InstrumentCatalog.All.Where(i =>
                i.Category == AssetCategory.Currency &&
                (i.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))];
        return Task.FromResult(matches);
    }

    public async Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        if (!TrySplitPair(instrument.Symbol, out var baseCur, out var quoteCur))
        {
            Log.Warn("Frankfurter", $"{instrument.Symbol}: not a 6-letter FX pair — no chart data");
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }

        // ECB is daily-only, so a sub-week window yields too few points to draw a line. Floor the
        // lookback at 7 days so even the 1D/1W tabs render a short daily series rather than a blank chart.
        var lookback = range.Lookback() < TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : range.Lookback();
        var series = await FetchSeriesAsync(baseCur, quoteCur, lookback, ct).ConfigureAwait(false);
        if (series is not { Count: > 0 })
        {
            Log.Warn("Frankfurter", $"{instrument.Symbol} candles: no data for {range}");
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }

        var points = series.Select(p => new CandlePoint(p.Date, p.Rate)).ToList();
        Log.Info("Frankfurter", $"{instrument.Symbol} candles -> {points.Count} pts");
        return new DomainCandleSeries(instrument.Symbol, range, points, IsValid: true);
    }

    private static async Task<DomainQuote> FetchQuoteAsync(DomainInstrument instrument, CancellationToken ct)
    {
        if (!TrySplitPair(instrument.Symbol, out var baseCur, out var quoteCur))
        {
            Log.Warn("Frankfurter", $"{instrument.Symbol}: not a 6-letter FX pair — treating as unavailable");
            return Invalid(instrument);
        }

        // A ~10-day window guarantees at least two business-day fixings across weekends/holidays, so we
        // can derive a day-over-day change. One call returns both the latest rate and the prior one.
        var series = await FetchSeriesAsync(baseCur, quoteCur, TimeSpan.FromDays(10), ct).ConfigureAwait(false);
        if (series is not { Count: > 0 })
            return Invalid(instrument);

        var price = series[^1].Rate;
        var prev = series.Count >= 2 ? series[^2].Rate : price;
        var change = price - prev;
        var pct = prev != 0m ? change / prev * 100m : 0m;

        var quote = new DomainQuote(
            instrument.Symbol, instrument.Name, instrument.Category, price, change, pct, IsValid: true);
        Log.Info("Frankfurter", $"{instrument.Symbol} -> rate={price} change%={pct}");
        return quote;
    }

    // Fetch the daily rate of quoteCur (per 1 baseCur) over [now-lookback, now], ascending by date.
    // Returns null on any HTTP / parse failure so the caller can degrade to an invalid quote/series.
    private static async Task<List<(DateTimeOffset Date, decimal Rate)>?> FetchSeriesAsync(
        string baseCur, string quoteCur, TimeSpan lookback, CancellationToken ct)
    {
        var to = DateTimeOffset.UtcNow;
        var from = to - lookback;
        // ISO dates carry no colons, and "{from}..{to}" is a single path segment (not a "../" dot-segment),
        // so it resolves cleanly under the /v1/ base address.
        var path = $"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}?base={baseCur}&symbols={quoteCur}";
        try
        {
            Log.Info("Frankfurter", $"GET /{from:yyyy-MM-dd}..{to:yyyy-MM-dd} {baseCur}->{quoteCur}");
            using var response = await Http.GetAsync(path, ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Frankfurter", $"{baseCur}{quoteCur} <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Frankfurter", $"{baseCur}{quoteCur}: non-success {(int)response.StatusCode} — no data");
                return null;
            }

            var dto = JsonSerializer.Deserialize(json, FrankfurterJsonContext.Default.ApiFrankfurterSeriesDto);
            if (dto?.Rates is not { Count: > 0 } rates)
                return null;

            // `rates` is keyed by ISO date; ISO dates sort lexically = chronologically. Pull our quote
            // currency out of each day's bucket, skipping any malformed entry.
            var points = new List<(DateTimeOffset Date, decimal Rate)>(rates.Count);
            foreach (var kv in rates.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                if (kv.Value.TryGetValue(quoteCur, out var rate) &&
                    DateTimeOffset.TryParse(
                        kv.Key, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                    points.Add((date, rate));
            }
            return points;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Frankfurter", $"{baseCur}{quoteCur}: request failed", ex);
            return null;
        }
    }

    // A neutral FX pair is 6 letters: base (first 3) + quote (last 3), e.g. EURUSD -> EUR, USD.
    private static bool TrySplitPair(string symbol, out string baseCur, out string quoteCur)
    {
        baseCur = quoteCur = string.Empty;
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length != 6)
            return false;
        baseCur = symbol[..3].ToUpperInvariant();
        quoteCur = symbol[3..].ToUpperInvariant();
        return true;
    }

    private static DomainQuote Invalid(DomainInstrument i) =>
        new(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false);
}

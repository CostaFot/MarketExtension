using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// Converts money between currencies using Frankfurter's keyless spot rates (the same ECB daily fixings the
// FX provider wraps). The Portfolio screen uses it to roll holdings priced in various native currencies up
// into the user's single PortfolioCurrency.
//
// Two-phase, because the screen renders synchronously but FX is a network fetch:
//   * PrimeAsync(preferred, natives) — fetch (and cache) every native→preferred rate that isn't already
//     fresh, in ONE batched Frankfurter call. Called off the price-load path (see PricedListPage's
//     OnPriceCacheUpdated hook), then the page re-renders.
//   * TryGetRate(from, to) — a synchronous cache read used while building the rows. Returns the rate, or
//     null when it isn't known yet OR the currency pair isn't ECB-supported (the row then shows native
//     value only and is excluded from the converted total). from == to short-circuits to 1.
//
// Rates are cached per ordered pair with a modest TTL: ECB publishes once per business day, so anything
// fresher than ~1h is pointless to refetch, and a stale entry simply re-primes on the next price refresh.
// Negative results (a currency ECB doesn't cover) are cached too, so we don't refetch them every tick.
internal sealed class CurrencyConverter
{
    public static readonly CurrencyConverter Instance = new();

    // Keyless, no documented rate limit — same host as FrankfurterMarketDataProvider. Single reused client.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.frankfurter.dev/v1/") };

    // Refetch a cached rate only once it's older than this. ECB is daily, so an hour is plenty fresh.
    private static readonly long TtlMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    // Key = "FROM>TO" (ISO codes, upper-cased). Value carries a nullable rate (null = resolved-as-
    // unsupported) plus the tick it was stored at, for TTL expiry.
    private readonly ConcurrentDictionary<string, CachedRate> _cache = new(StringComparer.Ordinal);

    // Demo-mode rate table: the USD value of 1 unit of each currency, for computing native→preferred as a
    // ratio with no network. Approximate mid-2025 levels — enough to exercise conversion, not to be precise.
    // Covers the PortfolioCurrency choices plus the common native quote currencies; a code that's missing
    // resolves to null (unsupported), mirroring how ECB-uncovered currencies behave live.
    private static readonly Dictionary<string, decimal> DemoUsdPerUnit = new(StringComparer.Ordinal)
    {
        ["USD"] = 1m,     ["EUR"] = 1.08m,  ["GBP"] = 1.27m,  ["JPY"] = 0.0063m, ["CHF"] = 1.12m,
        ["AUD"] = 0.66m,  ["CAD"] = 0.73m,  ["NZD"] = 0.61m,  ["CNY"] = 0.138m,  ["HKD"] = 0.128m,
        ["SGD"] = 0.74m,  ["SEK"] = 0.094m, ["NOK"] = 0.092m, ["DKK"] = 0.145m,  ["PLN"] = 0.25m,
        ["ZAR"] = 0.054m, ["MXN"] = 0.058m, ["INR"] = 0.012m, ["BRL"] = 0.18m,   ["KRW"] = 0.00073m,
    };

    // native→preferred under demo mode = (USD per native) / (USD per preferred). Null when either side
    // isn't in the table, matching the live "ECB doesn't cover it" → null contract.
    private static decimal? DemoRate(string from, string to) =>
        DemoUsdPerUnit.TryGetValue(from, out var f) && DemoUsdPerUnit.TryGetValue(to, out var t) && t != 0m
            ? f / t
            : null;

    private CurrencyConverter() { }

    private readonly record struct CachedRate(decimal? Rate, long Tick);

    private static string Key(string from, string to) => $"{from}>{to}";

    private bool TryGetFresh(string key, out decimal? rate)
    {
        rate = null;
        if (_cache.TryGetValue(key, out var entry) && Environment.TickCount64 - entry.Tick < TtlMs)
        {
            rate = entry.Rate;
            return true;
        }
        return false;
    }

    // Synchronous cache read for render time. Units of `to` per 1 unit of `from`, or null when unknown/
    // unsupported. Same currency → exactly 1 (no fetch, no rounding).
    public decimal? TryGetRate(string from, string to)
    {
        var f = (from ?? string.Empty).Trim().ToUpperInvariant();
        var t = (to ?? string.Empty).Trim().ToUpperInvariant();
        if (f.Length == 0 || t.Length == 0)
            return null;
        if (f == t)
            return 1m;
        return TryGetFresh(Key(f, t), out var rate) ? rate : null;
    }

    // Ensure native→preferred rates are cached for every `native`, fetching the not-yet-fresh ones in a
    // single Frankfurter call. Safe to call often: fresh entries (and same-currency natives) are skipped,
    // so a no-op call does no network. Failures are left uncached so the next call retries.
    public async Task PrimeAsync(string preferred, IEnumerable<string> natives, CancellationToken ct = default)
    {
        var to = (preferred ?? string.Empty).Trim().ToUpperInvariant();
        if (to.Length == 0)
            return;

        // Distinct native codes that still need fetching: not the preferred currency itself, and not
        // already fresh in the cache.
        var needed = natives
            .Select(n => (n ?? string.Empty).Trim().ToUpperInvariant())
            .Where(n => n.Length > 0 && n != to)
            .Distinct(StringComparer.Ordinal)
            .Where(n => !TryGetFresh(Key(n, to), out _))
            .ToList();

        if (needed.Count == 0)
            return;

        // Demo mode: fill the cache from the static table (no network), then return. TryGetRate reads the
        // cache exactly as it does for live rates, so the rest of the app is none the wiser.
        if (MarketSettingsManager.Instance.DemoMode)
        {
            var stamp = Environment.TickCount64;
            foreach (var native in needed)
                _cache[Key(native, to)] = new CachedRate(DemoRate(native, to), stamp);
            return;
        }

        try
        {
            // base=preferred → the response gives preferred→native rates; we invert each to native→preferred.
            // One call covers every needed native.
            var symbols = string.Join(",", needed);
            Log.Info("Convert", $"GET /latest base={to} symbols={symbols}");
            using var response = await Http.GetAsync($"latest?base={to}&symbols={symbols}", ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Convert", $"latest base={to} <- {(int)response.StatusCode} {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Convert", $"latest base={to}: non-success {(int)response.StatusCode} — leaving rates uncached");
                return;
            }

            var dto = JsonSerializer.Deserialize(json, FrankfurterJsonContext.Default.ApiFrankfurterLatestDto);
            var rates = dto?.Rates;
            var now = Environment.TickCount64;
            foreach (var native in needed)
            {
                // preferred→native present and non-zero → native→preferred = 1/that. Missing (ECB doesn't
                // cover it) → cache a null so we don't keep retrying within the TTL.
                decimal? nativeToPreferred =
                    rates is not null && rates.TryGetValue(native, out var prefToNative) && prefToNative != 0m
                        ? 1m / prefToNative
                        : null;
                _cache[Key(native, to)] = new CachedRate(nativeToPreferred, now);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Convert", $"latest base={to}: request failed", ex);
            // Leave uncached so a later prime retries.
        }
    }
}

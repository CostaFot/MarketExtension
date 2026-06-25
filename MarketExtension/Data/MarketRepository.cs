using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// The coordinator the UI depends on. Holds an ordered set of IMarketDataProviders (one per data
// source) and presents them as a single source of quotes: each instrument is routed to the first
// provider that supports its asset class, every provider's batch is fetched concurrently, and the
// results are merged into one list in the original instrument order. Instruments no provider can
// serve become invalid placeholders. Adding a data source = pass another provider to the
// constructor; nothing else changes.
internal sealed class MarketRepository(params IMarketDataProvider[] providers)
{
    // The providers that should serve right now. When any provider declares itself exclusive (e.g. the
    // mock in Demo mode), ONLY those serve — every operation routes to them alone, so the exclusive source
    // takes precedence everywhere, including the search fan-out that Supports() can't gate. Otherwise the
    // full ordered set, routed normally by first-match Supports.
    private IReadOnlyList<IMarketDataProvider> ActiveProviders()
    {
        var exclusive = providers.Where(p => p.IsExclusive).ToList();
        return exclusive.Count > 0 ? exclusive : providers;
    }

    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        // Route each instrument to the first ACTIVE provider that can serve its asset class.
        var active = ActiveProviders();
        var batches = new Dictionary<IMarketDataProvider, List<DomainInstrument>>();
        var unserviceable = new List<DomainInstrument>();
        foreach (var instrument in instruments)
        {
            var provider = active.FirstOrDefault(p => p.Supports(instrument.Category));
            if (provider is null)
            {
                unserviceable.Add(instrument);
                continue;
            }

            if (!batches.TryGetValue(provider, out var batch))
                batches[provider] = batch = [];
            batch.Add(instrument);
        }

        // One batched call per provider, fanned out concurrently.
        var results = await Task.WhenAll(
            batches.Select(kv => kv.Key.GetQuotesAsync(kv.Value, ct))).ConfigureAwait(false);

        // Merge into a symbol -> quote lookup; instruments no provider serves become invalid
        // placeholders so the row renders as unavailable rather than vanishing.
        var bySymbol = results.SelectMany(r => r).ToDictionary(q => q.Symbol);
        foreach (var instrument in unserviceable)
            bySymbol[instrument.Symbol] =
                new DomainQuote(instrument.Symbol, instrument.Name, instrument.Category, 0m, 0m, 0m, IsValid: false);

        // Preserve the caller's instrument order.
        var ordered = instruments.Select(i => bySymbol[i.Symbol]).ToList();
        Log.Info("Repository",
            $"{instruments.Count} instrument(s) via {batches.Count} provider(s); " +
            $"{ordered.Count(q => q.IsValid)} valid, {unserviceable.Count} unserviceable");
        return ordered;
    }

    // Free-text instrument lookup. Fans out to every provider and merges their matches into one
    // list, deduped by symbol and preserving first-seen (provider) order. Identity only — callers
    // fetch quotes separately, so a search stays one call per provider regardless of match count.
    public async Task<IReadOnlyList<DomainInstrument>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            ActiveProviders().Select(p => p.SearchAsync(query, ct))).ConfigureAwait(false);

        var merged = results
            .SelectMany(r => r)
            .GroupBy(i => i.Symbol, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Log.Info("Repository", $"search '{query}' -> {merged.Count} merged result(s)");
        return merged;
    }

    // Historical candles for one instrument over a ChartRange — routed to the first provider that
    // supports its asset class (mirrors GetQuotesAsync). No provider → an invalid series so the chart
    // renders an "unavailable" state rather than throwing.
    public async Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        var provider = ActiveProviders().FirstOrDefault(p => p.Supports(instrument.Category));
        if (provider is null)
        {
            Log.Info("Repository", $"candles {instrument.Symbol} {range}: no provider for {instrument.Category}");
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }

        var series = await provider.GetCandlesAsync(instrument, range, ct).ConfigureAwait(false);
        Log.Info("Repository",
            $"candles {instrument.Symbol} {range} -> {(series.HasData ? $"{series.Points.Count} pts" : "no data")}");
        return series;
    }
}

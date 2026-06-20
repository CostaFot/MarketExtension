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
    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        // Route each instrument to the first provider that can serve its asset class.
        var batches = new Dictionary<IMarketDataProvider, List<DomainInstrument>>();
        var unserviceable = new List<DomainInstrument>();
        foreach (var instrument in instruments)
        {
            var provider = providers.FirstOrDefault(p => p.Supports(instrument.Category));
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
}

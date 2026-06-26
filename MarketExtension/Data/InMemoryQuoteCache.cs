using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;

namespace MarketExtension;

// In-memory IQuoteCache: one MutableStateFlow<DomainQuote?> per normalized symbol, lazily created.
// The single shared store of live quotes; MarketRepository owns one instance for the whole process.
// Mirrors the WatchlistStore state-holder idiom — a Lock guarding a dictionary, snapshot under the
// lock then Update() OUTSIDE it (Update fans handlers out that may re-enter Get/Observe, and
// System.Threading.Lock is non-reentrant, so updating under the lock could deadlock a re-entrant
// handler).
//
// Swap this for a database-backed IQuoteCache later via MarketRepository's injectable ctor overload —
// no repository or UI change.
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Owns per-symbol MutableStateFlow<DomainQuote?> (BehaviorSubject-backed) entries for " +
                    "the life of the process via the single MarketRepository; they are intentionally never " +
                    "completed or disposed — mirrors the StateFlow singleton convention (see StateFlow.cs).")]
internal sealed class InMemoryQuoteCache : IQuoteCache
{
    private readonly Lock _lock = new();

    // key = WatchlistStore.Normalize(symbol). Grows only with distinct observed symbols (watchlist +
    // favorites + portfolio membership — tens), so no eviction is needed.
    private readonly Dictionary<string, MutableStateFlow<DomainQuote?>> _entries = [];

    public DomainQuote? Get(string symbol)
    {
        lock (_lock)
            return _entries.TryGetValue(WatchlistStore.Normalize(symbol), out var flow) ? flow.Value : null;
    }

    public StateFlow<DomainQuote?> Observe(string symbol)
    {
        lock (_lock)
            return GetOrCreate(WatchlistStore.Normalize(symbol));
    }

    public void Upsert(DomainQuote quote, bool keepLastGood = true)
    {
        var key = WatchlistStore.Normalize(quote.Symbol);

        MutableStateFlow<DomainQuote?> flow;
        DomainQuote? next = quote;
        lock (_lock)
        {
            flow = GetOrCreate(key);
            // Keep-last-good: a transient invalid quote must not overwrite a price that was fine.
            // Decide the value to write under the lock (reads flow.Value); Update fires outside.
            if (keepLastGood && !quote.IsValid && flow.Value is { IsValid: true })
                next = flow.Value; // Update below no-ops (distinct-until-changed)
        }

        flow.Update(next); // fan handlers out OUTSIDE the lock (handlers re-read the cache)
    }

    public void Clear()
    {
        List<MutableStateFlow<DomainQuote?>> flows;
        lock (_lock)
            flows = [.. _entries.Values]; // keep entries so observers stay subscribed; reset the values

        foreach (var flow in flows)
            flow.Update(null);
    }

    // Caller must hold _lock.
    private MutableStateFlow<DomainQuote?> GetOrCreate(string key)
    {
        if (!_entries.TryGetValue(key, out var flow))
            _entries[key] = flow = new MutableStateFlow<DomainQuote?>(null); // default comparer = record value equality
        return flow;
    }
}

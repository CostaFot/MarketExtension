using System;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using DynamicData;

namespace MarketExtension;

// In-memory IQuoteCacheDataSource backed by DynamicData's SourceCache<QuoteEntity, string> — a reactive
// keyed cache (the .NET analog of Android Room's "@Query → Flow"): writes go through AddOrUpdate, surfaces
// OBSERVE a key's value as an IObservable that re-emits on every change. The single shared store of live
// quotes; MarketRepository owns one instance for the whole process. Keyed by WatchlistStore.Normalize.
//
// Why DynamicData rather than the hand-rolled per-symbol BehaviorSubject dictionary it replaces:
//   * Upsert's keep-last-good (read current -> decide -> write) is the one operation that MUST be atomic,
//     else two concurrent write-throughs (poll loop, observe-subscribe fetch, demo flip — no repo-side
//     generation guard) can both read the same pre-write value and let a transient invalid quote land
//     last, defeating the guard. SourceCache.Edit runs its lambda under the cache's lock, so the
//     read-decide-write is atomic by construction — no app-level lock, and the change notification is
//     fanned out by DynamicData AFTER Edit returns (not under our lock), so there is no "fan-out under a
//     lock" deadlock question to reason about.
//   * Absence is modelled as a removed key, not a null-valued entry: Clear() removes all keys, and an
//     Observe stream maps a Remove back to null so subscribers see the "loading" state while staying
//     subscribed.
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SourceCache is owned by the single process-lifetime MarketRepository and is " +
                    "observed for the life of the process; it is intentionally never disposed (mirrors the " +
                    "StateFlow singleton convention — see StateFlow.cs).")]
internal sealed class InMemoryQuoteCacheDataSource : IQuoteCacheDataSource
{
    // Keyed by WatchlistStore.Normalize(symbol) — the key selector runs on every AddOrUpdate, so every
    // lookup/observe must normalize its input symbol to match. Grows only with distinct observed symbols
    // (watchlist + favorites + portfolio membership — tens), so no eviction is needed.
    private readonly SourceCache<QuoteEntity, string> _cache =
        new(q => WatchlistStore.Normalize(q.Symbol));

    public QuoteEntity? Get(string symbol)
    {
        var found = _cache.Lookup(WatchlistStore.Normalize(symbol));
        return found.HasValue ? found.Value : null;
    }

    public IObservable<QuoteEntity?> Observe(string symbol)
    {
        var key = WatchlistStore.Normalize(symbol);

        // Defer so the StartWith snapshot is read at SUBSCRIBE time, not when Observe is called. Connect()
        // replays the cache's current state to each new subscriber, so Watch(key) emits the current value
        // on subscribe when the key is present; map a Remove (Clear) back to null; StartWith seeds the
        // null/absent case (and is deduped against Watch's replay by DistinctUntilChanged). Net contract:
        // null until first Upsert, then each distinct value, null again on Clear — subscription stays live.
        return Observable.Defer(() => _cache.Connect()
            .Watch(key)
            .Select(change => change.Reason == ChangeReason.Remove ? null : (QuoteEntity?)change.Current)
            .StartWith(Get(symbol))
            .DistinctUntilChanged());
    }

    public void Upsert(QuoteEntity quote, bool keepLastGood = true)
    {
        var key = WatchlistStore.Normalize(quote.Symbol);

        // Edit runs ATOMICALLY under the cache's lock — the read (Lookup), the keep-last-good decision, and
        // the write (AddOrUpdate) are one indivisible operation, so concurrent write-throughs can't clobber
        // each other (this is the whole reason for the cache to "own" keep-last-good).
        _cache.Edit(updater =>
        {
            var current = updater.Lookup(key);

            // Keep-last-good: a transient invalid quote must not overwrite a price that was fine.
            if (keepLastGood && !quote.IsValid && current is { HasValue: true, Value.IsValid: true })
                return;

            // Distinct-until-changed at the source: an identical re-fetch is a no-op (no change emitted),
            // matching the old BehaviorSubject behaviour so an unchanged poll doesn't wake subscribers.
            if (current.HasValue && current.Value == quote)
                return;

            updater.AddOrUpdate(quote);
        });
    }

    // Reset every entry to null while keeping observers subscribed: removing the keys makes each live
    // Observe stream emit a Remove -> null, and a later Upsert re-adds -> the stream emits the new value.
    public void Clear() => _cache.Clear();
}

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;

namespace MarketExtension;

// In-memory INewsCacheDataSource backed by DynamicData's SourceCache<NewsFeedEntity, NewsCategory> — a
// reactive keyed cache (the .NET analog of Android Room's "@Query → Flow"). Each key is a NewsCategory; each
// value is that category's feed, a NewsFeedEntity wrapping the NewsEntity list. The wrapper exists ONLY so
// the list can be keyed by category inside the SourceCache — it never crosses the interface, which speaks
// NewsEntity lists. The single shared store of cached news; MarketRepository owns one instance for the whole
// process.
//
// Mirrors InMemoryQuoteCacheDataSource, and for the same reason: Upsert's keep-last-good (read current →
// decide → write) MUST be atomic, else two concurrent write-throughs (poll loop, observe-subscribe fetch,
// demo flip) could both read the same pre-write value and let a transient empty result land last, defeating
// the guard. SourceCache.Edit runs its lambda under the cache's lock, so the read-decide-write is atomic by
// construction — no app-level lock — and DynamicData fans the change out AFTER Edit returns. Absence is
// modelled as a removed key: Clear() removes all keys, and an Observe stream maps a Remove back to an empty
// list so subscribers see the "loading" state while staying subscribed.
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SourceCache is owned by the single process-lifetime MarketRepository and is " +
                    "observed for the life of the process; it is intentionally never disposed (mirrors the " +
                    "InMemoryQuoteCacheDataSource / StateFlow singleton convention).")]
internal sealed class InMemoryNewsCacheDataSource : INewsCacheDataSource
{
    // One entry per category; only ever the four NewsCategory values, so no eviction is needed.
    private readonly SourceCache<NewsFeedEntity, NewsCategory> _cache = new(f => f.Category);

    // A shared empty instance returned by Get() for an absent/cleared category — a stable "no feed" snapshot
    // for the count-based staleness checks (NeedsNewsFetchOnSubscribe). Observe() uses NULL for that case
    // instead (see GetOrNull), so it can distinguish "not fetched" from "fetched-but-empty".
    private static readonly IReadOnlyList<NewsEntity> Empty = [];

    public IReadOnlyList<NewsEntity> Get(NewsCategory category)
    {
        var found = _cache.Lookup(category);
        return found.HasValue ? found.Value.Items : Empty;
    }

    public IObservable<IReadOnlyList<NewsEntity>?> Observe(NewsCategory category)
    {
        // Defer so the StartWith snapshot is read at SUBSCRIBE time, not when Observe is called. Connect()
        // replays the cache's current state to each new subscriber, so Watch(category) emits the current feed
        // on subscribe when the key is present; a Remove (Clear) or an ABSENT key maps to NULL (= not fetched
        // yet / cleared — the "loading" state), which a present-but-EMPTY feed does not. StartWith seeds the
        // absent case with null (deduped against Watch's replay by DistinctUntilChanged — present feeds hand
        // back the same Items reference). Net contract: null until the first Upsert, then each distinct feed
        // (empty included), null again on Clear — subscription stays live.
        return Observable.Defer(() => _cache.Connect()
            .Watch(category)
            .Select(change => change.Reason == ChangeReason.Remove ? null : (IReadOnlyList<NewsEntity>?)change.Current.Items)
            .StartWith(GetOrNull(category))
            .DistinctUntilChanged());
    }

    // The current cached feed, or NULL when the category was never fetched / has been cleared (absent key) —
    // the snapshot Observe seeds with. Get() returns an empty list for that case instead, for count checks.
    private IReadOnlyList<NewsEntity>? GetOrNull(NewsCategory category)
    {
        var found = _cache.Lookup(category);
        return found.HasValue ? found.Value.Items : null;
    }

    public void Upsert(NewsCategory category, IReadOnlyList<NewsEntity> news, bool keepLastGood = true)
    {
        // Edit runs ATOMICALLY under the cache's lock — the read (Lookup), the keep-last-good decision, and
        // the write (AddOrUpdate) are one indivisible operation, so concurrent write-throughs can't clobber
        // each other (this is the whole reason for the cache to "own" keep-last-good).
        _cache.Edit(updater =>
        {
            var current = updater.Lookup(category);

            // Keep-last-good: a transient empty result must not wipe a feed that was fine.
            if (keepLastGood && news.Count == 0 && current is { HasValue: true, Value.Items.Count: > 0 })
                return;

            // Distinct-until-changed at the source: an identical re-fetch is a no-op (no change emitted), so
            // an unchanged poll doesn't wake subscribers. NewsEntity is a value-equal record, so SequenceEqual
            // compares element-by-element.
            if (current.HasValue && current.Value.Items.SequenceEqual(news))
                return;

            updater.AddOrUpdate(new NewsFeedEntity(category, news));
        });
    }

    // Reset every category to empty while keeping observers subscribed: removing the keys makes each live
    // Observe stream emit a Remove -> empty, and a later Upsert re-adds -> the stream emits the new feed.
    public void Clear() => _cache.Clear();

    // The keyed cache item: a category and its feed. Exists only so the NewsEntity list can be keyed by
    // category inside the SourceCache; never crosses the interface boundary.
    private sealed record NewsFeedEntity(NewsCategory Category, IReadOnlyList<NewsEntity> Items);
}

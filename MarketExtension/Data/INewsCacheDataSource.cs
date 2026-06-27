using System;
using System.Collections.Generic;

namespace MarketExtension;

// A process-wide store of the latest market-news feed per NewsCategory, exposed as OBSERVABLE state every
// surface can subscribe to — so a news page (and any other news surface) reads the SAME cached feed and they
// can't drift apart. The news analog of IQuoteCacheDataSource: where the quote cache holds one QuoteEntity
// per symbol, this holds one LIST of NewsEntity per category (news is inherently a list). Keyed by
// NewsCategory.
//
// This is the news CACHE DATA SOURCE — one of the sources MarketRepository orchestrates over (alongside the
// IMarketDataProviders). It's an interface so the storage mechanism stays an implementation detail: the
// in-memory implementation can be swapped for a database-backed one later without touching MarketRepository
// or any UI surface. MarketRepository owns one instance, writes through to it on every news fetch, and news
// surfaces OBSERVE it instead of fetching independently.
//
// Data layer: holds NewsEntity (the storage model, no formatting), NOT DomainNews. MarketRepository maps
// NewsEntity <-> DomainNews at its boundary, so the entity never escapes the data source; surfaces still
// observe DomainNews off the repository and project it to a Ui* model as the quote surfaces do.
internal interface INewsCacheDataSource
{
    // Current cached feed for a category, or an empty list if it was never fetched / has been cleared.
    // Synchronous snapshot read.
    IReadOnlyList<NewsEntity> Get(NewsCategory category);

    // Per-category observable feed: replays the current value (empty until the first fetch lands) on
    // subscribe, then pushes each change. Distinct-until-changed (an identical re-fetch does NOT re-emit —
    // Upsert skips a value-equal write at the source). This is the seam a surface waits on for the first
    // fetch: subscribe → empty (render a spinner/placeholder) → the feed when Upsert lands. A Clear()
    // re-emits empty while keeping the subscription live.
    IObservable<IReadOnlyList<NewsEntity>> Observe(NewsCategory category);

    // Write a freshly fetched feed through to the cache for a category. keepLastGood:true (the default)
    // drops a transient EMPTY result (e.g. a network error or rate-limit mapped to no items) when a
    // non-empty feed is already cached — the news analog of the quote cache's keep-last-good (which guards a
    // valid quote against a transient invalid one). keepLastGood:false overwrites unconditionally (a hard
    // refresh / data-source flip).
    void Upsert(NewsCategory category, IReadOnlyList<NewsEntity> news, bool keepLastGood = true);

    // Reset every category's feed to empty, KEEPING observer subscriptions live (so observers re-emit the
    // empty "loading" state and a re-fetch refills). Used when the data SOURCE flips (demo mode) so news
    // from the old source can neither linger nor be preserved by keep-last-good.
    void Clear();
}

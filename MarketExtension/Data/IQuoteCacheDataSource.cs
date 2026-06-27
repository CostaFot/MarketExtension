namespace MarketExtension;

// A process-wide store of the latest QuoteEntity per symbol, exposed as OBSERVABLE state every surface
// can subscribe to — so two surfaces showing the same symbol read the SAME entry and can never drift
// apart (the out-of-sync prices the favorites dock and the screens show today). Keyed by
// WatchlistStore.Normalize, the one cache key used everywhere.
//
// This is the quote CACHE DATA SOURCE — one of the sources MarketRepository orchestrates over (alongside
// the IMarketDataProviders). It's an interface so the storage mechanism stays an implementation detail:
// the in-memory implementation can be swapped for a database-backed one later (the "local cache that will
// probably be a database layer") without touching MarketRepository or any UI surface. MarketRepository
// owns one instance, writes through to it on every fetch, and every priced surface OBSERVES it instead of
// fetching independently.
//
// Data layer: holds QuoteEntity (the storage model, no formatting), NOT DomainQuote. MarketRepository maps
// QuoteEntity <-> DomainQuote at its boundary, so the entity never escapes the data source; surfaces still
// observe DomainQuote off the repository and project it to UiQuote as they do today.
internal interface IQuoteCacheDataSource
{
    // Current cached quote for a symbol, or null if it was never fetched / has been cleared.
    // Synchronous snapshot read.
    QuoteEntity? Get(string symbol);

    // Per-symbol observable entry: replays the current value (null until the first fetch lands) on
    // subscribe, then pushes each change. Lazily created on first access. Distinct-until-changed via
    // QuoteEntity value equality (an identical re-fetch does NOT re-emit). This is the seam a surface
    // waits on for the first fetch: subscribe → null (render a spinner) → the quote when Upsert lands.
    StateFlow<QuoteEntity?> Observe(string symbol);

    // Write a freshly fetched quote through to the cache. keepLastGood:true (the default) drops a
    // transient invalid quote (e.g. a 429 mapped to IsValid:false) when a valid quote is already
    // cached — the SINGLE home for the "keep last good" guard currently copy-pasted across the priced
    // surfaces. keepLastGood:false overwrites unconditionally (a hard refresh / data-source flip).
    void Upsert(QuoteEntity quote, bool keepLastGood = true);

    // Reset every entry to null, KEEPING observer subscriptions live (so observers re-emit a "loading"
    // state and a re-fetch refills). Used when the data SOURCE flips (demo mode) so prices from the old
    // source can neither linger nor be preserved by keep-last-good.
    void Clear();
}

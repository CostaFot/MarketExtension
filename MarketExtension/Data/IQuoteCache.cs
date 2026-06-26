namespace MarketExtension;

// A process-wide store of the latest DomainQuote per symbol, exposed as OBSERVABLE state every surface
// can subscribe to — so two surfaces showing the same symbol read the SAME entry and can never drift
// apart (the out-of-sync prices the favorites dock and the screens show today). Keyed by
// WatchlistStore.Normalize, the one cache key used everywhere.
//
// This is an interface so the in-memory implementation now can be swapped for a database-backed one
// later (the "local cache that will probably be a database layer") without touching MarketRepository or
// any UI surface. MarketRepository owns one instance and writes through to it on every fetch; surfaces
// will later OBSERVE it instead of fetching independently (not done yet — see the cache layer plan).
//
// Domain layer: holds DomainQuote (no formatting). Surfaces project to UiQuote as they do today.
internal interface IQuoteCache
{
    // Current cached quote for a symbol, or null if it was never fetched / has been cleared.
    // Synchronous snapshot read.
    DomainQuote? Get(string symbol);

    // Per-symbol observable entry: replays the current value (null until the first fetch lands) on
    // subscribe, then pushes each change. Lazily created on first access. Distinct-until-changed via
    // DomainQuote value equality (an identical re-fetch does NOT re-emit). This is the seam a surface
    // waits on for the first fetch: subscribe → null (render a spinner) → the quote when Upsert lands.
    StateFlow<DomainQuote?> Observe(string symbol);

    // Write a freshly fetched quote through to the cache. keepLastGood:true (the default) drops a
    // transient invalid quote (e.g. a 429 mapped to IsValid:false) when a valid quote is already
    // cached — the SINGLE home for the "keep last good" guard currently copy-pasted across the priced
    // surfaces. keepLastGood:false overwrites unconditionally (a hard refresh / data-source flip).
    void Upsert(DomainQuote quote, bool keepLastGood = true);

    // Reset every entry to null, KEEPING observer subscriptions live (so observers re-emit a "loading"
    // state and a re-fetch refills). Used when the data SOURCE flips (demo mode) so prices from the old
    // source can neither linger nor be preserved by keep-last-good.
    void Clear();
}

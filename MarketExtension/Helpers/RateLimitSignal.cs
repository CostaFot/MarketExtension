namespace MarketExtension;

// A process-wide "are we currently being throttled" flag. Providers report through it at the HTTP seam
// (see HttpRetry): a request that stays 429 after backing off flips it ON; any request that ultimately
// succeeds flips it OFF. Priced surfaces subscribe to IsRateLimited and show an advisory banner row
// (RateLimitHint) so the user understands why prices look stale, instead of silently seeing the
// keep-last-good prices with no explanation.
//
// Modeled on the StateFlow state-holders (WatchlistStore, MarketSettingsManager): a single
// MutableStateFlow<bool> behind a read-only StateFlow<bool> face, distinct-until-changed so redundant
// reports don't wake subscribers. It is intentionally GLOBAL, not per-symbol: a free-tier rate limit
// applies to the whole API key, so the condition is shared across every instrument and surface — a global
// signal models that exactly (and avoids rippling a per-quote status through all four providers + UiQuote).
internal sealed class RateLimitSignal
{
    public static readonly RateLimitSignal Instance = new();

    private readonly MutableStateFlow<bool> _isRateLimited = new(false);

    private RateLimitSignal()
    {
        // In demo mode no live request is ever made, so a "throttled" signal is meaningless — and because
        // nothing calls ReportSuccess() offline, a signal set before demo mode was switched on would
        // otherwise linger. Clear it the moment demo mode turns on, so we start clean and stay clean while
        // demoing; turning demo back off leaves it cleared, so the banner only returns on a fresh live 429.
        // replay:false — only react to a flip, not the value at construction.
        MarketSettingsManager.Instance.DemoModeChanged.Subscribe(
            demo => { if (demo) ReportSuccess(); }, replayOnSubscribe: false);
    }

    // Observable "are recent requests being throttled" flag. Read .Value for a snapshot, or Subscribe to
    // re-render when it flips (the priced pages do the latter to show/hide the banner reactively).
    public StateFlow<bool> IsRateLimited => _isRateLimited;

    // A request was throttled even after HttpRetry backed off (or a provider saw an in-body 429).
    // Distinct-until-changed means this only wakes subscribers on the false -> true edge.
    public void ReportRateLimited()
    {
        if (_isRateLimited.Update(true))
            Log.Warn("RateLimit", "throttled — surfaces will show the rate-limited banner");
    }

    // A request ultimately succeeded, so we're not hard-throttled right now — clear the banner. Called by
    // HttpRetry on any 2xx. Only emits on the true -> false edge.
    public void ReportSuccess()
    {
        if (_isRateLimited.Update(false))
            Log.Info("RateLimit", "request succeeded — clearing the rate-limited banner");
    }
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// The live-price poll ticker — the concrete realization of the "PolledStateFlow" seam StateFlow<T> was
// built for (its OnActive/OnInactive subscriber-count hooks = Kotlin's WhileSubscribed / Rx RefCount()).
//
// It is a StateFlow<long> that emits a monotonically increasing tick on a timer WHILE at least one
// surface is subscribed, and stops the timer when the last one unsubscribes. Priced surfaces
// (PricedListPage, FavoritesDockPage) subscribe with replayOnSubscribe:false and re-price their current
// set on each tick. A plain counter (not the instrument list) is used because the membership flows dedup
// identical symbol sequences (distinct-until-changed) — an incrementing long always gets through.
//
// One process-wide instance is shared by every priced surface, so the timer lives while ANY of them is
// visible and goes quiet only when they all hide (a pinned dock band keeps it warm). The loop re-reads
// the interval from MarketSettingsManager each iteration, so a settings change applies without a reload;
// when auto-refresh is off (interval 0) it idles on a short re-check so toggling it on later resumes.
//
// AOT/trim-safe: only Task.Delay / CancellationTokenSource / System.Threading.Lock — no reflection, no
// JSON, no new serializer context (see CLAUDE.md AOT section).
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime singleton; the CTS is created/disposed per active-poll cycle in " +
                    "OnActive/OnInactive and the instance itself is never disposed.")]
internal sealed class PollTicker : StateFlow<long>
{
    public static readonly PollTicker Instance = new();

    // How long to idle before re-checking settings while auto-refresh is off, so the user can turn it on
    // mid-view and have polling resume without reloading the extension.
    private static readonly TimeSpan OffRecheckInterval = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;

    private PollTicker() : base(0) { }

    // First subscriber arrived: (re)start the poll loop. Cancelling any prior CTS first guards a rapid
    // inactive -> active bounce from leaving two loops running.
    protected override void OnActive()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _ = PollLoop(_cts.Token);
        }

        Log.Info("Poll", "Poll loop started — a priced surface became visible");
    }

    // Last subscriber left: stop the loop. The in-flight Task.Delay throws OperationCanceledException,
    // which PollLoop swallows.
    protected override void OnInactive()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        Log.Info("Poll", "Poll loop stopped — all priced surfaces hidden");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Re-read live each iteration so an interval/on-off change applies without a reload.
                var settings = MarketSettingsManager.Instance;
                var delay = settings.AutoRefreshEnabled ? settings.RefreshInterval : OffRecheckInterval;

                await Task.Delay(delay, ct).ConfigureAwait(false);

                if (settings.AutoRefreshEnabled)
                {
                    SetValue(Value + 1); // emit a tick -> every visible priced surface re-prices in place
                    Log.Info("Poll", $"Tick #{Value} — signalling visible surfaces to refresh");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled in OnInactive (or on a rapid restart) — just stop.
        }
    }
}

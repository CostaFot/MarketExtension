using System;
using System.Reactive.Linq;

namespace MarketExtension;

// The live-price poll ticker — emits a tick WHILE at least one priced surface is subscribed and stops
// when the last one unsubscribes. Priced surfaces (PricedListPage, FavoritesDockPage, the SymbolDetailPage
// chart) subscribe and re-price their current set on each tick.
//
// Pure Rx (no longer a StateFlow<long>):
//   * Observable.Generate drives a self-rescheduling timer whose per-step delay is re-read from
//     MarketSettingsManager each iteration, so an interval / on-off change applies without a reload. When
//     auto-refresh is off it idles on a short re-check and the tick is filtered out (Where), so toggling
//     it back on later resumes on the next iteration.
//   * Publish().RefCount() IS the WhileSubscribed / RefCount() seam the old OnActive/OnInactive hooks
//     hand-rolled: the Generate loop starts on the first subscriber (0 -> 1) and is torn down on the last
//     unsubscribe (1 -> 0). Generate's condition is always true so the source never completes — disposal
//     is therefore silent (no OnCompleted/OnError reaches the shared subject), so a later resubscribe
//     cleanly restarts the loop. Defer logs the start; Finally logs the stop, both at the refcount edges.
//
// This replaces the hand-rolled StateFlow<long> + CancellationTokenSource version: the tick-counter/dedup
// workaround, the manual subscriber-count refcount, the CTS lifecycle + lock, and the CA1001 suppression
// are all now Rx's job. (The tick value restarts from 0 on each active cycle — only ever used for a Debug
// log line, never read by subscribers.)
//
// Subscribe via PollTicker.Subscribe(onTick): the handler is guarded so a synchronous throw can't escape
// into the shared stream. This matters because the published stream is multicast — an unguarded throw
// would be caught by Rx's SafeObserver, which DISPOSES that subscription AND rethrows back through the
// Publish subject, terminating the stream for EVERY surface (polling dead until reload). The guard
// swallows + logs (Log.Error) instead. Handlers should still offload heavy work (all PollRefresh()s Task.Run),
// but they no longer have to be throw-proof.
//
// ⚠️ That guard covers the SUBSCRIBER side only. The SOURCE operators below (timeSelector, Where, iterate,
// Do) are NOT guarded: a throw in any of them OnErrors the Publish subject, poisoning it for every surface
// (polling dead until reload — the exact failure mode above). They are safe today because they only do
// non-throwing MarketSettingsManager property reads; keep them that way. Any new logic here that could
// throw (e.g. parsing a malformed setting) must guard itself or use .Catch/.Retry.
internal static class PollTicker
{
    // How long to idle before re-checking settings while auto-refresh is off, so the user can turn it on
    // mid-view and have polling resume without reloading the extension.
    private static readonly TimeSpan OffRecheckInterval = TimeSpan.FromSeconds(30);

    // One process-wide ticker shared by every priced surface, so the timer lives while ANY of them is
    // visible and goes quiet only when they all hide (a pinned dock band keeps it warm). Private so every
    // subscription goes through the guarded Subscribe below — no surface can attach an unguarded handler.
    private static readonly IObservable<long> Ticks =
        Observable.Defer(() =>
            {
                Log.Info("Poll", "Poll loop started — a priced surface became visible");
                return Observable
                    .Generate(
                        initialState: 0L,
                        condition: _ => true,
                        iterate: tick => tick + 1,
                        resultSelector: tick => tick,
                        timeSelector: _ =>
                        {
                            // Re-read live each iteration so an interval/on-off change applies without a reload.
                            var settings = MarketSettingsManager.Instance;
                            return settings.AutoRefreshEnabled ? settings.RefreshInterval : OffRecheckInterval;
                        })
                    .Where(_ => MarketSettingsManager.Instance.AutoRefreshEnabled)
                    .Do(tick => Log.Info("Poll", $"Tick #{tick} — signalling visible surfaces to refresh"));
            })
            .Finally(() => Log.Info("Poll", "Poll loop stopped — all priced surfaces hidden"))
            .Publish()
            .RefCount();

    // Run onTick on every poll tick while subscribed. The handler is wrapped so a throw is contained
    // (swallowed + logged) and can't tear down the shared stream — see the type comment above.
    public static IDisposable Subscribe(Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        return Ticks.Subscribe(_ =>
        {
            try { onTick(); }
            catch (Exception ex) { Log.Error("Poll", "tick handler threw — continuing poll loop", ex); }
        });
    }
}

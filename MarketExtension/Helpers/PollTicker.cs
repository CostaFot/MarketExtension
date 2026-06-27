using System;
using System.Reactive.Linq;

namespace MarketExtension;

// The market-data poll tickers — each emits a tick WHILE at least one subscriber is active and stops when
// the last one unsubscribes. There are TWO, on independent cadences read from MarketSettingsManager:
//   * the PRICE ticker (Subscribe) — driven by RefreshInterval / AutoRefreshEnabled. The repository's quote
//     poll loop and the SymbolDetailPage chart subscribe to it.
//   * the NEWS ticker (SubscribeNews) — driven by NewsRefreshInterval / NewsAutoRefreshEnabled, a SEPARATE
//     (gentler) clock so news refreshes don't piggyback on how aggressively prices refresh. The repository's
//     news poll loop subscribes to it.
// Both are built by the same BuildTicker, so the careful Rx lifecycle below is shared, parameterized only by
// which settings drive the cadence; each is refcounted independently (news polls only while a news surface
// is active, prices only while a priced surface is).
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
// Subscribe via Subscribe(onTick) / SubscribeNews(onTick): the handler is guarded so a synchronous throw
// can't escape into the shared stream. This matters because each published stream is multicast — an
// unguarded throw would be caught by Rx's SafeObserver, which DISPOSES that subscription AND rethrows back
// through the Publish subject, terminating the stream for EVERY subscriber on that ticker (its polling dead
// until reload). The guard swallows + logs (Log.Error) instead. Handlers should still offload heavy work
// (all PollRefresh()s Task.Run), but they no longer have to be throw-proof.
//
// ⚠️ That guard covers the SUBSCRIBER side only. The SOURCE operators in BuildTicker (timeSelector, Where,
// iterate, Do) are NOT guarded: a throw in any of them OnErrors the Publish subject, poisoning it for every
// subscriber (polling dead until reload — the exact failure mode above). They are safe today because they
// only do non-throwing MarketSettingsManager property reads; keep them that way. Any new logic here that
// could throw (e.g. parsing a malformed setting) must guard itself or use .Catch/.Retry.
internal static class PollTicker
{
    // How long to idle before re-checking settings while a ticker's auto-refresh is off, so the user can
    // turn it on mid-view and have polling resume without reloading the extension.
    private static readonly TimeSpan OffRecheckInterval = TimeSpan.FromSeconds(30);

    // The price ticker — its cadence is the price RefreshInterval. Shared by every priced surface, so the
    // timer lives while ANY of them is active and goes quiet only when they all stop (a pinned dock keeps it
    // warm). Private so every subscription goes through the guarded Subscribe below.
    private static readonly IObservable<long> PriceTicks =
        BuildTicker("Poll", s => s.AutoRefreshEnabled, s => s.RefreshInterval);

    // The news ticker — same machinery, on the SEPARATE news cadence (NewsRefreshInterval), refcounted
    // independently, so news polls only while a news surface is active.
    private static readonly IObservable<long> NewsTicks =
        BuildTicker("NewsPoll", s => s.NewsAutoRefreshEnabled, s => s.NewsRefreshInterval);

    // Run onTick on every PRICE poll tick while subscribed. The handler is wrapped so a throw is contained
    // (swallowed + logged) and can't tear down the shared stream — see the type comment above.
    public static IDisposable Subscribe(Action onTick) => SubscribeGuarded(PriceTicks, onTick, "Poll");

    // Run onTick on every NEWS poll tick while subscribed (same guard as Subscribe, separate cadence).
    public static IDisposable SubscribeNews(Action onTick) => SubscribeGuarded(NewsTicks, onTick, "NewsPoll");

    // Build a guarded, refcounted ticker whose cadence is re-read live from settings via the two selectors.
    private static IObservable<long> BuildTicker(
        string tag, Func<MarketSettingsManager, bool> enabled, Func<MarketSettingsManager, TimeSpan> interval)
        => Observable.Defer(() =>
            {
                Log.Info(tag, "Poll loop started — a subscriber became active");
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
                            return enabled(settings) ? interval(settings) : OffRecheckInterval;
                        })
                    .Where(_ => enabled(MarketSettingsManager.Instance))
                    .Do(tick => Log.Info(tag, $"Tick #{tick} — signalling active subscribers to refresh"));
            })
            .Finally(() => Log.Info(tag, "Poll loop stopped — all subscribers inactive"))
            .Publish()
            .RefCount();

    // Subscribe with the throw-containing guard (see the type comment): a throwing handler is swallowed +
    // logged so it can't tear down the shared multicast stream.
    private static IDisposable SubscribeGuarded(IObservable<long> ticks, Action onTick, string tag)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        return ticks.Subscribe(_ =>
        {
            try { onTick(); }
            catch (Exception ex) { Log.Error(tag, "tick handler threw — continuing poll loop", ex); }
        });
    }
}

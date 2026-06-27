using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// The coordinator the UI depends on. Holds an ordered set of IMarketDataProviders (one per data
// source) and presents them as a single source of quotes: each instrument is routed to the first
// provider that supports its asset class, every provider's batch is fetched concurrently, and the
// results are merged into one list in the original instrument order. Instruments no provider can
// serve become invalid placeholders. Adding a data source = pass another provider to the
// constructor; nothing else changes.
//
// It also owns the shared IQuoteCacheDataSource: every fetch writes through to it (so the cache fills with no
// surface changes), and surfaces can OBSERVE a set of instruments as one cache-backed list observable
// instead of each fetching independently (the orchestration API — ObserveQuotes/RefreshAsync). This is
// how two surfaces showing the same symbol stop drifting apart.
//
// Polling lives HERE, not per surface: the repository runs ONE poll loop (a single PollTicker
// subscription for its lifetime) that, each tick, refreshes the distinct union of all instruments
// currently being observed through the cache — so the observed prices stay fresh from one shared fetch.
// Every priced QUOTE surface (the Watchlist/Favorites/Portfolio pages + both dock bands) is now a pure
// observer through here — none fetch or poll themselves. The lone exception is the symbol-detail CHART,
// a separate candle path (GetCandlesAsync) with no cache/observe layer: it still subscribes PollTicker
// directly, which is fine since only one detail page is ever open (no cross-surface drift to fix).
internal sealed class MarketRepository
{
    private readonly IMarketDataProvider[] _providers;
    private readonly IQuoteCacheDataSource _cacheSource;

    // Refcounted registry of the instruments currently being observed via ObserveQuotes — keyed by
    // Normalize(symbol), each carrying the latest DomainInstrument (needed for provider routing) and a
    // subscriber count. The poll loop refreshes the distinct union each tick; a symbol observed by two
    // surfaces has count 2 and is fetched once.
    private readonly Lock _observedLock = new();
    private readonly Dictionary<string, ObservedInstrument> _observed = [];

    // Per-symbol last fetch-ATTEMPT time (Environment.TickCount64), keyed by Normalize(symbol). Stamped on
    // every write-through — success OR a kept-last-good failure — so it reflects "last time we tried", which
    // is what staleness means. A subscribe reads it (NeedsFetchOnSubscribe) to tell a stale cached price from
    // a fresh one and refresh only the stale ones: the central home for the freshness clock the priced pages
    // used to keep per-surface (RefreshStaleQuotes). Bounded by tracked symbols (tens) like the cache itself,
    // so no eviction is needed.
    private readonly ConcurrentDictionary<string, long> _lastFetchTicks = new();

    // Default: an in-memory cache. The call site in MarketExtensionCommandsProvider uses this.
    public MarketRepository(params IMarketDataProvider[] providers)
        : this(new InMemoryQuoteCacheDataSource(), providers) { }

    // Injectable cache — for a future database-backed IQuoteCacheDataSource (and tests). No other code changes.
    public MarketRepository(IQuoteCacheDataSource cacheSource, IMarketDataProvider[] providers)
    {
        _cacheSource = cacheSource;
        _providers = providers;

        // A demo-mode flip swaps the data SOURCE, so every cached price is now wrong (it came from the other
        // source). Clear it, then refresh whatever is currently observed so visible surfaces repaint from the
        // new source at once (a hidden surface refetches on its next open via ObserveQuotes' fetch-missing).
        // replay:false — construction is a no-op (the cache is empty then). Subscribed before any surface;
        // never disposed (process-lifetime).
        _ = MarketSettingsManager.Instance.DemoModeChanged.Subscribe(_ => OnDemoModeFlip(), replayOnSubscribe: false);

        // The single poll loop. While the repository is alive (the process lifetime), each PollTicker tick
        // refreshes the union of currently-observed instruments (RefreshObserved) in one batched fetch and
        // writes the results back through the cache, so every observing surface repaints in sync. PollTicker
        // honors the refresh-interval setting and idles when it's off (and an empty observed set is a no-op),
        // so a permanent subscription is safe. Never disposed — process-lifetime, like the cache itself.
        _ = PollTicker.Subscribe(RefreshObserved);
    }

    // The providers that should serve right now. When any provider declares itself exclusive (e.g. the
    // mock in Demo mode), ONLY those serve — every operation routes to them alone, so the exclusive source
    // takes precedence everywhere, including the search fan-out that Supports() can't gate. Otherwise the
    // full ordered set, routed normally by first-match Supports.
    private IReadOnlyList<IMarketDataProvider> ActiveProviders()
    {
        var exclusive = _providers.Where(p => p.IsExclusive).ToList();
        return exclusive.Count > 0 ? exclusive : _providers;
    }

    // Route → fetch (concurrent, one batch per provider) → merge into the caller's instrument order.
    // The raw data path with no cache side effects, used by RefreshAsync.
    private async Task<IReadOnlyList<DomainQuote>> FetchMergeAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct)
    {
        // Route each instrument to the first ACTIVE provider that can serve its asset class.
        var active = ActiveProviders();
        var batches = new Dictionary<IMarketDataProvider, List<DomainInstrument>>();
        var unserviceable = new List<DomainInstrument>();
        foreach (var instrument in instruments)
        {
            var provider = active.FirstOrDefault(p => p.Supports(instrument.Category));
            if (provider is null)
            {
                unserviceable.Add(instrument);
                continue;
            }

            if (!batches.TryGetValue(provider, out var batch))
                batches[provider] = batch = [];
            batch.Add(instrument);
        }

        // One batched call per provider, fanned out concurrently.
        var results = await Task.WhenAll(
            batches.Select(kv => kv.Key.GetQuotesAsync(kv.Value, ct))).ConfigureAwait(false);

        // Merge into a symbol -> quote lookup; instruments no provider serves become invalid
        // placeholders so the row renders as unavailable rather than vanishing.
        var bySymbol = results.SelectMany(r => r).ToDictionary(q => q.Symbol);
        foreach (var instrument in unserviceable)
            bySymbol[instrument.Symbol] =
                new DomainQuote(instrument.Symbol, instrument.Name, instrument.Category, 0m, 0m, 0m, IsValid: false);

        // Preserve the caller's instrument order.
        var ordered = instruments.Select(i => bySymbol[i.Symbol]).ToList();
        Log.Info("Repository",
            $"{instruments.Count} instrument(s) via {batches.Count} provider(s); " +
            $"{ordered.Count(q => q.IsValid)} valid, {unserviceable.Count} unserviceable");
        return ordered;
    }

    // Force a fetch now; results reach observers through the cache (the caller does not read the return).
    // The single fetch seam — the manual Refresh row, the demo flip, the observe-subscribe fetch, and the
    // poll loop all call it (via RefreshSafe for the fire-and-forget paths).
    // keepLastGood:false = a HARD refresh that overwrites even with an invalid quote (e.g. after a source flip,
    // where a stale "good" value would be wrong); the default keeps the last good price through a bad fetch.
    public async Task RefreshAsync(
        IReadOnlyList<DomainInstrument> instruments, bool keepLastGood = true, CancellationToken ct = default)
    {
        var ordered = await FetchMergeAsync(instruments, ct).ConfigureAwait(false);
        WriteThrough(ordered, keepLastGood);
    }

    // Write a freshly fetched batch through to the cache AND stamp each symbol's last fetch-attempt time, so a
    // later subscribe can distinguish a stale cached price from a fresh one (see NeedsFetchOnSubscribe). The
    // single write-through path; called by RefreshAsync (the one fetch path).
    private void WriteThrough(IReadOnlyList<DomainQuote> ordered, bool keepLastGood)
    {
        var now = Environment.TickCount64;
        foreach (var quote in ordered)
        {
            _cacheSource.Upsert(QuoteEntity.From(quote), keepLastGood); // domain → storage at the boundary
            _lastFetchTicks[WatchlistStore.Normalize(quote.Symbol)] = now;
        }
    }

    // Observe a FIXED set of instruments as one cache-backed list observable. Both public overloads append
    // ObserveOn(TaskPoolScheduler) — this is the DEADLOCK FIX. Without it, a cache write-through fans out
    // synchronously and the CombineLatest/Switch combiner calls the subscriber's handler (ultimately a
    // surface's RaiseItemsChanged → a blocking COM call into Command Palette's STA) WHILE Rx still holds the
    // combiner gate lock; the host then re-enters the extension and the gate/STA lock order cycles → hang.
    // ObserveOn hands each emission to the scheduler, so subscribers are notified only AFTER the Rx gate
    // locks are released — no host call ever runs under a producer-side lock. (Surfaces already expect
    // off-host-thread notifications: the toolkit marshals RaiseItemsChanged, same as the priced pages' Task.Run.)
    public IObservable<IReadOnlyList<DomainQuote>> ObserveQuotes(IReadOnlyList<DomainInstrument> instruments)
        => ObserveQuotesCore(instruments).ObserveOn(TaskPoolScheduler.Default);

    // Membership-aware: re-projects (Switch) whenever the set changes — fetching newly-added symbols and
    // dropping departed ones for free. ObserveOn AFTER Switch so the band is notified off the Switch gate too
    // (Core is used here, not the public overload, so there's a single ObserveOn hop, after Switch).
    public IObservable<IReadOnlyList<DomainQuote>> ObserveQuotes(
        StateFlow<IReadOnlyList<DomainInstrument>> instruments)
        => instruments.AsObservable().Select(set => ObserveQuotesCore(set)).Switch()
            .ObserveOn(TaskPoolScheduler.Default);

    // The cache-backed list observable WITHOUT the ObserveOn hop — the raw graph. On subscribe: register the
    // instruments as observed (so the poll loop keeps them fresh) and fetch any not-yet-cached OR gone-stale
    // symbols (write-through, fire-and-forget; see NeedsFetchOnSubscribe). Then CombineLatest the per-symbol
    // cache flows into one ordered list
    // (symbols not yet loaded — null entries — are dropped) that re-emits whenever any member quote changes.
    // On dispose: unregister. Observable.Create (not Defer) so the dispose hook can unregister. The public
    // overloads above add ObserveOn so the host is never called under the combiner gate (see that note).
    private IObservable<IReadOnlyList<DomainQuote>> ObserveQuotesCore(IReadOnlyList<DomainInstrument> instruments)
        => Observable.Create<IReadOnlyList<DomainQuote>>(observer =>
        {
            Register(instruments);
            // Fetch the symbols this subscribe should refresh: never-cached ones (we must render something)
            // plus — when auto-refresh is on — any whose cached price has aged past one interval while
            // unobserved. The poll loop keeps OBSERVED symbols fresh; this catches a hidden surface reopening
            // on a stale cache, the central replacement for the priced pages' old per-surface RefreshStaleQuotes.
            var toFetch = instruments.Where(NeedsFetchOnSubscribe).ToList();
            if (toFetch.Count > 0)
            {
                // Re-derive the missing/stale split for the log only (NeedsFetchOnSubscribe stays the single
                // decision): a to-fetch symbol is "missing" if it's still uncached, else it's a stale refresh.
                var missing = toFetch.Count(i => _cacheSource.Get(i.Symbol) is null);
                Log.Info("Repository",
                    $"observe subscribe: refreshing {toFetch.Count}/{instruments.Count} " +
                    $"({missing} missing, {toFetch.Count - missing} stale) [{string.Join(", ", toFetch.Select(i => i.Symbol))}]");
                _ = RefreshSafe(toFetch); // fire-and-forget; writes through → observers see it land
            }
            else if (instruments.Count > 0)
            {
                Log.Info("Repository", $"observe subscribe: all {instruments.Count} symbol(s) fresh in cache — no fetch");
            }

            IObservable<IReadOnlyList<DomainQuote>> inner = instruments.Count == 0
                ? Observable.Return<IReadOnlyList<DomainQuote>>([])
                : Observable
                    .CombineLatest(instruments.Select(i => _cacheSource.Observe(i.Symbol).AsObservable()))
                    // storage → domain at the boundary; OfType drops the null (not-yet-loaded) entries.
                    .Select(entities => (IReadOnlyList<DomainQuote>)entities
                        .OfType<QuoteEntity>().Select(e => e.ToDomainQuote()).ToList());

            return new CompositeDisposable(
                inner.Subscribe(observer),
                Disposable.Create(() => Unregister(instruments)));
        });

    // RefreshAsync with its exceptions swallowed + logged — for the fire-and-forget fetch-missing path and
    // the poll loop, where a provider throw would otherwise become an unobserved task exception.
    private async Task RefreshSafe(IReadOnlyList<DomainInstrument> instruments)
    {
        try { await RefreshAsync(instruments).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error("Repository", "background refresh failed", ex); }
    }

    // Should this instrument be (re)fetched at subscribe time? Never-cached → always (we must render
    // something). Otherwise only when auto-refresh is on AND the cached price has aged past one refresh
    // interval — i.e. it went stale while no surface was observing it. With auto-refresh off (interval 0) an
    // already-cached price is left as-is, so "off" spends no calls. Reads the stamp WriteThrough sets.
    private bool NeedsFetchOnSubscribe(DomainInstrument instrument)
    {
        if (_cacheSource.Get(instrument.Symbol) is null)
            return true;

        var settings = MarketSettingsManager.Instance;
        if (!settings.AutoRefreshEnabled)
            return false;

        // Cached but somehow unstamped (e.g. a value that survived a cache Clear) → refresh to be safe.
        if (!_lastFetchTicks.TryGetValue(WatchlistStore.Normalize(instrument.Symbol), out var ticks))
            return true;

        return TimeSpan.FromMilliseconds(Environment.TickCount64 - ticks) >= settings.RefreshInterval;
    }

    // --- The poll loop + the observed-instrument registry ----------------------------------------------

    // One PollTicker tick: refresh the distinct union of all currently-observed instruments in one batched
    // fetch (keep-last-good via RefreshAsync's default, so a transient bad poll doesn't blank a good price).
    // Nothing observed → nothing to do.
    private void RefreshObserved()
    {
        var instruments = ObservedInstruments();
        if (instruments.Count == 0)
            return;
        Log.Info("Repository", $"poll tick: refreshing {instruments.Count} observed instrument(s)");
        _ = RefreshSafe(instruments);
    }

    // Demo mode flipped the data source: drop the now-wrong cached prices, then refill the observed set from
    // the new source so visible surfaces repaint immediately (post-Clear all entries are null, so this writes
    // the new-source quotes through unconditionally).
    private void OnDemoModeFlip()
    {
        _cacheSource.Clear();
        var observed = ObservedInstruments();
        Log.Info("Repository",
            $"demo mode flipped — cache cleared; refreshing {observed.Count} observed instrument(s) from the new source");
        if (observed.Count > 0)
            _ = RefreshSafe(observed);
    }

    // Called when a surface subscribes to ObserveQuotes: ref up each instrument so the poll loop fetches it.
    private void Register(IReadOnlyList<DomainInstrument> instruments)
    {
        lock (_observedLock)
            foreach (var instrument in instruments)
            {
                var key = WatchlistStore.Normalize(instrument.Symbol);
                if (_observed.TryGetValue(key, out var entry))
                {
                    entry.Count++;
                    entry.Instrument = instrument; // keep the latest identity (name/category)
                }
                else
                {
                    _observed[key] = new ObservedInstrument(instrument);
                }
            }
    }

    // Called when an ObserveQuotes subscription is disposed: ref down, dropping symbols no surface observes.
    private void Unregister(IReadOnlyList<DomainInstrument> instruments)
    {
        lock (_observedLock)
            foreach (var instrument in instruments)
            {
                var key = WatchlistStore.Normalize(instrument.Symbol);
                if (_observed.TryGetValue(key, out var entry) && --entry.Count <= 0)
                    _observed.Remove(key);
            }
    }

    // Snapshot of the distinct instruments with at least one observer — what the poll loop refreshes.
    private IReadOnlyList<DomainInstrument> ObservedInstruments()
    {
        lock (_observedLock)
            return [.. _observed.Values.Select(e => e.Instrument)];
    }

    // A registry entry: the latest observed identity for a symbol + how many subscriptions reference it.
    private sealed class ObservedInstrument(DomainInstrument instrument)
    {
        public DomainInstrument Instrument { get; set; } = instrument;
        public int Count { get; set; } = 1;
    }

    // Free-text instrument lookup. Fans out to every provider and merges their matches into one
    // list, deduped by symbol and preserving first-seen (provider) order. Identity only — callers
    // fetch quotes separately, so a search stays one call per provider regardless of match count.
    public async Task<IReadOnlyList<DomainInstrument>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            ActiveProviders().Select(p => p.SearchAsync(query, ct))).ConfigureAwait(false);

        var merged = results
            .SelectMany(r => r)
            .GroupBy(i => i.Symbol, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        Log.Info("Repository", $"search '{query}' -> {merged.Count} merged result(s)");
        return merged;
    }

    // Historical candles for one instrument over a ChartRange — routed to the first provider that
    // supports its asset class (mirrors the quote routing). No provider → an invalid series so the chart
    // renders an "unavailable" state rather than throwing.
    public async Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        var provider = ActiveProviders().FirstOrDefault(p => p.Supports(instrument.Category));
        if (provider is null)
        {
            Log.Info("Repository", $"candles {instrument.Symbol} {range}: no provider for {instrument.Category}");
            return DomainCandleSeries.Invalid(instrument.Symbol, range);
        }

        var series = await provider.GetCandlesAsync(instrument, range, ct).ConfigureAwait(false);
        Log.Info("Repository",
            $"candles {instrument.Symbol} {range} -> {(series.HasData ? $"{series.Points.Count} pts" : "no data")}");
        return series;
    }
}

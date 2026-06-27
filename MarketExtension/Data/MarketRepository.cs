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
//
// NEWS is orchestrated the same way (see the "News orchestration" section): the repository owns one shared
// INewsCacheDataSource, every news fetch writes through, and surfaces ObserveNews(category) instead of
// fetching — so a future news screen and news dock band read the SAME cached feed and stay in sync, exactly
// as the priced surfaces do for quotes. A SECOND poll loop (the news PollTicker, on the separate news
// cadence) refreshes the observed categories each tick. News isn't routed by asset class: it goes to the
// first active provider whose SupportsNews is true (Finnhub live; the mock in Demo mode).
internal sealed class MarketRepository
{
    private readonly IMarketDataProvider[] _providers;
    private readonly IQuoteCacheDataSource _cacheSource;
    private readonly INewsCacheDataSource _newsCacheSource;

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

    // Refcounted registry of the news categories currently being observed via ObserveNews (the news analog of
    // _observed). The news poll loop refreshes each registered category every tick; a category observed by two
    // surfaces has count 2 and is fetched once per tick.
    private readonly Lock _observedNewsLock = new();
    private readonly Dictionary<NewsCategory, int> _observedNews = [];

    // Per-category last news fetch-ATTEMPT time (Environment.TickCount64). The news analog of _lastFetchTicks;
    // a subscribe reads it (NeedsNewsFetchOnSubscribe) to refresh only a stale-or-missing category's feed.
    private readonly ConcurrentDictionary<NewsCategory, long> _lastNewsFetchTicks = new();

    // Default: in-memory quote + news caches. The call site in MarketExtensionCommandsProvider uses this.
    public MarketRepository(params IMarketDataProvider[] providers)
        : this(new InMemoryQuoteCacheDataSource(), new InMemoryNewsCacheDataSource(), providers) { }

    // Injectable caches — for future database-backed IQuoteCacheDataSource / INewsCacheDataSource (and tests).
    // No other code changes.
    public MarketRepository(
        IQuoteCacheDataSource cacheSource, INewsCacheDataSource newsCacheSource, IMarketDataProvider[] providers)
    {
        _cacheSource = cacheSource;
        _newsCacheSource = newsCacheSource;
        _providers = providers;

        // A demo-mode flip swaps the data SOURCE, so every cached price (and news feed) is now wrong (it came
        // from the other source). Clear them, then refresh whatever is currently observed so visible surfaces
        // repaint from the new source at once (a hidden surface refetches on its next open via the
        // observe-subscribe fetch). replay:false — construction is a no-op (the caches are empty then).
        // Subscribed before any surface; never disposed (process-lifetime).
        _ = MarketSettingsManager.Instance.DemoModeChanged.Subscribe(_ => OnDemoModeFlip(), replayOnSubscribe: false);

        // The single price poll loop. While the repository is alive (the process lifetime), each PollTicker
        // tick refreshes the union of currently-observed instruments (RefreshObserved) in one batched fetch
        // and writes the results back through the cache, so every observing surface repaints in sync.
        // PollTicker honors the refresh-interval setting and idles when it's off (and an empty observed set is
        // a no-op), so a permanent subscription is safe. Never disposed — process-lifetime, like the cache.
        _ = PollTicker.Subscribe(RefreshObserved);

        // The NEWS poll loop — its own PollTicker subscription on the SEPARATE news cadence. Each tick
        // refreshes the currently-observed news categories (RefreshObservedNews). Same lifetime/no-dispose
        // rationale as the price loop above.
        _ = PollTicker.SubscribeNews(RefreshObservedNews);
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
                    .CombineLatest(instruments.Select(i => _cacheSource.Observe(i.Symbol)))
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
            $"demo mode flipped — quote cache cleared; refreshing {observed.Count} observed instrument(s) from the new source");
        if (observed.Count > 0)
            _ = RefreshSafe(observed);

        // The news feed came from the other source too — clear it and refill the observed categories.
        _newsCacheSource.Clear();
        var observedNews = ObservedNewsCategories();
        Log.Info("Repository",
            $"demo mode flipped — news cache cleared; refreshing {observedNews.Count} observed categor(ies) from the new source");
        foreach (var category in observedNews)
            _ = RefreshNewsSafe(category);
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

    // --- News orchestration (mirrors the quote orchestration above) ------------------------------------
    //
    // News is a market-wide feed, not per-instrument data, so it isn't routed by Supports(AssetCategory): it
    // goes to the first ACTIVE provider whose SupportsNews is true (Finnhub live; the mock in Demo mode). Each
    // NewsCategory is cached + observed independently, so surfaces ObserveNews(category) instead of fetching —
    // the future news screen and the news dock band then read the SAME cached feed and stay in sync, exactly
    // as the priced surfaces do for quotes. The repository owns the single news poll loop (the SubscribeNews
    // ticker, on the separate news cadence) refreshing the observed categories each tick.

    // The provider that serves news right now: the first ACTIVE one that supports it (Finnhub normally; the
    // mock when Demo mode makes it exclusive). null only if no active provider serves news.
    private IMarketDataProvider? NewsProvider() => ActiveProviders().FirstOrDefault(p => p.SupportsNews);

    // Fetch the latest news for a category from the news provider. No provider → empty. The raw data path
    // with no cache side effects, used by RefreshNewsAsync. (minId 0 = the latest batch; the cache replaces
    // the category's feed on write-through, so each refresh re-reads the head of the feed.)
    private async Task<IReadOnlyList<DomainNews>> FetchNewsAsync(NewsCategory category, CancellationToken ct)
    {
        var provider = NewsProvider();
        if (provider is null)
        {
            Log.Info("Repository", $"news {category}: no active provider serves news");
            return [];
        }

        var news = await provider.GetNewsAsync(category, minId: 0, ct).ConfigureAwait(false);
        Log.Info("Repository", $"news {category} -> {news.Count} item(s)");
        return news;
    }

    // Force a news fetch now; results reach observers through the news cache (the caller does not read the
    // return). The single news fetch seam — the demo flip, the observe-subscribe fetch, the poll loop, and a
    // surface's manual refresh all call it (via RefreshNewsSafe for the fire-and-forget paths).
    // keepLastGood:false overwrites even with an empty result (e.g. after a source flip); the default keeps
    // the last good feed through a transient empty fetch.
    public async Task RefreshNewsAsync(
        NewsCategory category, bool keepLastGood = true, CancellationToken ct = default)
    {
        var news = await FetchNewsAsync(category, ct).ConfigureAwait(false);
        WriteThroughNews(category, news, keepLastGood);
    }

    // Write a freshly fetched feed through to the news cache AND stamp the category's last fetch-attempt time
    // (so a later subscribe can tell a stale feed from a fresh one — see NeedsNewsFetchOnSubscribe). Maps
    // domain → storage at the boundary. The single news write-through path; called by RefreshNewsAsync.
    private void WriteThroughNews(NewsCategory category, IReadOnlyList<DomainNews> news, bool keepLastGood)
    {
        _newsCacheSource.Upsert(category, [.. news.Select(NewsEntity.From)], keepLastGood);
        _lastNewsFetchTicks[category] = Environment.TickCount64;
    }

    // Observe one category's news feed as a cache-backed list observable. Like ObserveQuotes it appends
    // ObserveOn(TaskPoolScheduler) — the same DEADLOCK FIX: a cache write-through fans out synchronously, and
    // without the hop a surface's RaiseItemsChanged (a blocking COM call into Command Palette's STA) could run
    // while Rx still holds a producer-side gate, cycling the gate/STA lock order → hang. ObserveOn hands each
    // emission to the scheduler, so surfaces are notified only AFTER the gate locks release. (Surfaces already
    // expect off-host-thread notifications — the toolkit marshals RaiseItemsChanged.)
    public IObservable<IReadOnlyList<DomainNews>> ObserveNews(NewsCategory category)
        => ObserveNewsCore(category).ObserveOn(TaskPoolScheduler.Default);

    // Selection-aware: re-projects (Switch) whenever the chosen category changes — fetching the newly-selected
    // category and dropping the previous one's observation for free. For a news screen with category tabs
    // backed by a StateFlow. ObserveOn AFTER Switch so the surface is notified off the Switch gate too (Core
    // is used here, not the public overload, so there's a single ObserveOn hop, after Switch).
    public IObservable<IReadOnlyList<DomainNews>> ObserveNews(StateFlow<NewsCategory> category)
        => category.AsObservable().Select(c => ObserveNewsCore(c)).Switch()
            .ObserveOn(TaskPoolScheduler.Default);

    // The cache-backed feed observable WITHOUT the ObserveOn hop — the raw graph. On subscribe: register the
    // category as observed (so the news poll loop keeps it fresh) and fetch if the cached feed is missing or
    // gone stale (write-through, fire-and-forget; see NeedsNewsFetchOnSubscribe). Then map the cache's
    // NewsEntity feed → DomainNews on each emission. On dispose: unregister. Observable.Create (not Defer) so
    // the dispose hook can unregister. The public overloads above add ObserveOn so the host is never called
    // under a producer-side gate (see that note).
    private IObservable<IReadOnlyList<DomainNews>> ObserveNewsCore(NewsCategory category)
        => Observable.Create<IReadOnlyList<DomainNews>>(observer =>
        {
            RegisterNews(category);
            if (NeedsNewsFetchOnSubscribe(category))
            {
                Log.Info("Repository", $"observe news subscribe: refreshing {category}");
                _ = RefreshNewsSafe(category); // fire-and-forget; writes through → observers see it land
            }
            else
            {
                Log.Info("Repository", $"observe news subscribe: {category} fresh in cache — no fetch");
            }

            // The cache's per-category Observe replays the current feed (empty until the first fetch) then
            // pushes each change; storage → domain at the boundary.
            var inner = _newsCacheSource.Observe(category)
                .Select(entities => (IReadOnlyList<DomainNews>)entities.Select(e => e.ToDomainNews()).ToList());

            return new CompositeDisposable(
                inner.Subscribe(observer),
                Disposable.Create(() => UnregisterNews(category)));
        });

    // RefreshNewsAsync with its exceptions swallowed + logged — for the fire-and-forget fetch + poll paths,
    // where a provider throw would otherwise become an unobserved task exception.
    private async Task RefreshNewsSafe(NewsCategory category)
    {
        try { await RefreshNewsAsync(category).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error("Repository", $"background news refresh failed for {category}", ex); }
    }

    // Should this category be (re)fetched at subscribe time? Never-cached / empty → always (we must render
    // something). Otherwise only when news auto-refresh is on AND the cached feed aged past one news interval
    // while unobserved — the news analog of NeedsFetchOnSubscribe. With news auto-refresh off (interval 0) an
    // already-cached feed is left as-is.
    private bool NeedsNewsFetchOnSubscribe(NewsCategory category)
    {
        if (_newsCacheSource.Get(category).Count == 0)
            return true;

        var settings = MarketSettingsManager.Instance;
        if (!settings.NewsAutoRefreshEnabled)
            return false;

        if (!_lastNewsFetchTicks.TryGetValue(category, out var ticks))
            return true;

        return TimeSpan.FromMilliseconds(Environment.TickCount64 - ticks) >= settings.NewsRefreshInterval;
    }

    // One news poll tick: refresh every currently-observed category (one /news call each — news isn't
    // batchable across categories like quotes are across symbols). Nothing observed → nothing to do.
    private void RefreshObservedNews()
    {
        var categories = ObservedNewsCategories();
        if (categories.Count == 0)
            return;
        Log.Info("Repository", $"news poll tick: refreshing {categories.Count} observed categor(ies)");
        foreach (var category in categories)
            _ = RefreshNewsSafe(category);
    }

    // Ref up a category when a surface subscribes to ObserveNews, so the news poll loop keeps it fresh.
    private void RegisterNews(NewsCategory category)
    {
        lock (_observedNewsLock)
            _observedNews[category] = _observedNews.GetValueOrDefault(category) + 1;
    }

    // Ref down a category when an ObserveNews subscription is disposed, dropping it once no surface observes it.
    private void UnregisterNews(NewsCategory category)
    {
        lock (_observedNewsLock)
        {
            if (!_observedNews.TryGetValue(category, out var count))
                return;
            if (--count <= 0)
                _observedNews.Remove(category);
            else
                _observedNews[category] = count;
        }
    }

    // Snapshot of the distinct categories with at least one observer — what the news poll loop refreshes.
    private IReadOnlyList<NewsCategory> ObservedNewsCategories()
    {
        lock (_observedNewsLock)
            return [.. _observedNews.Keys];
    }
}

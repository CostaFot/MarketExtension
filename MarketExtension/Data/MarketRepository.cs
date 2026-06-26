using System;
using System.Collections.Generic;
using System.Linq;
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
// It also owns the shared IQuoteCache: every fetch writes through to it (so the cache fills with no
// surface changes), and surfaces can OBSERVE a set of instruments as one cache-backed list observable
// instead of each fetching independently (the orchestration API — ObserveQuotes/RefreshAsync). This is
// how two surfaces showing the same symbol stop drifting apart. Migrating the surfaces onto it is a
// later pass; for now only write-through runs.
internal sealed class MarketRepository
{
    private readonly IMarketDataProvider[] _providers;
    private readonly IQuoteCache _cache;

    // Default: an in-memory cache. The call site in MarketExtensionCommandsProvider uses this.
    public MarketRepository(params IMarketDataProvider[] providers)
        : this(new InMemoryQuoteCache(), providers) { }

    // Injectable cache — for a future database-backed IQuoteCache (and tests). No other code changes.
    public MarketRepository(IQuoteCache cache, IMarketDataProvider[] providers)
    {
        _cache = cache;
        _providers = providers;

        // A demo-mode flip swaps the data SOURCE, so every cached price is now wrong (it came from the
        // other source). Clear so it can neither linger nor be preserved by keep-last-good. replay:false —
        // construction is a no-op (the cache is empty then). Subscribed here, before any surface, so on a
        // flip this Clear runs before a surface's own re-fetch. Never disposed: the repository lives for
        // the whole process.
        _ = MarketSettingsManager.Instance.DemoModeChanged.Subscribe(_ => _cache.Clear(), replayOnSubscribe: false);
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

    // Fetch quotes for a set of instruments AND write them through to the shared cache. Keeps its exact
    // public signature/behavior (callers still get the ordered list back); the cache fill is a side effect,
    // so every existing caller populates the cache with no change to it.
    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        var ordered = await FetchMergeAsync(instruments, ct).ConfigureAwait(false);
        foreach (var quote in ordered)
            _cache.Upsert(quote); // keep-last-good
        return ordered;
    }

    // Route → fetch (concurrent, one batch per provider) → merge into the caller's instrument order.
    // The raw data path with no cache side effects, shared by GetQuotesAsync and RefreshAsync.
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
    // The seam the manual Refresh row / demo path call now and the orchestration poll loop will call later.
    // keepLastGood:false = a HARD refresh that overwrites even with an invalid quote (e.g. after a source flip,
    // where a stale "good" value would be wrong); the default keeps the last good price through a bad fetch.
    public async Task RefreshAsync(
        IReadOnlyList<DomainInstrument> instruments, bool keepLastGood = true, CancellationToken ct = default)
    {
        var ordered = await FetchMergeAsync(instruments, ct).ConfigureAwait(false);
        foreach (var quote in ordered)
            _cache.Upsert(quote, keepLastGood);
    }

    // Observe a FIXED set of instruments as one cache-backed list observable: on subscribe, fetch any
    // not-yet-cached symbols once (write-through, fire-and-forget), then CombineLatest the per-symbol cache
    // flows into one ordered list (symbols not yet loaded — null entries — are dropped) that re-emits
    // whenever any member quote changes. Defer runs the fetch-missing per subscribe.
    public IObservable<IReadOnlyList<DomainQuote>> ObserveQuotes(IReadOnlyList<DomainInstrument> instruments)
        => Observable.Defer(() =>
        {
            var missing = instruments.Where(i => _cache.Get(i.Symbol) is null).ToList();
            if (missing.Count > 0)
                _ = RefreshSafe(missing); // fire-and-forget; writes through → observers see it land

            if (instruments.Count == 0)
                return Observable.Return<IReadOnlyList<DomainQuote>>([]);

            return Observable
                .CombineLatest(instruments.Select(i => _cache.Observe(i.Symbol).AsObservable()))
                .Select(quotes => (IReadOnlyList<DomainQuote>)quotes.OfType<DomainQuote>().ToList());
        });

    // Membership-aware: re-projects (Switch) whenever the set changes — fetching newly-added symbols and
    // dropping departed ones for free. The shape the priced pages / dock bands will consume next pass.
    public IObservable<IReadOnlyList<DomainQuote>> ObserveQuotes(
        StateFlow<IReadOnlyList<DomainInstrument>> instruments)
        => instruments.AsObservable().Select(set => ObserveQuotes(set)).Switch();

    // RefreshAsync with its exceptions swallowed + logged — for the fire-and-forget fetch-missing path,
    // where a provider throw would otherwise become an unobserved task exception.
    private async Task RefreshSafe(IReadOnlyList<DomainInstrument> instruments)
    {
        try { await RefreshAsync(instruments).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error("Repository", "background refresh failed", ex); }
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
    // supports its asset class (mirrors GetQuotesAsync). No provider → an invalid series so the chart
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

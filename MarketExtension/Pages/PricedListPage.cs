using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Shared base for the two priced list screens (Watchlist, Favorites). Each OBSERVES a different
// WatchlistStore membership flow (passed to the ctor) and prices its instruments. Wiring:
//
//   * The page subscribes to its instrument flow in the INotifyItemsChanged `add` accessor (CmdPal's
//     de-facto "page became visible" hook) and disposes in `remove`. StateFlow replays the current set
//     on subscribe, which drives the initial price load — so navigating here always re-prices.
//   * When membership changes (a row added/removed from anywhere), the flow pushes the new set and the
//     page reconciles LOCALLY against a per-symbol price cache: rows that left are dropped with no
//     network call, and only newly-added symbols are fetched. This keeps edits cheap against the
//     Finnhub rate limit and makes a removed row disappear at once (rather than lingering until reload).
//   * The Refresh row (and, later, the live-poll timer) forces a full re-price of the whole set.
//
// Typing only re-filters the already-loaded quotes locally — these screens never hit the network on
// keystroke (only the SearchPage talks to /search, and only on Enter).
internal abstract partial class PricedListPage : DynamicListPage, INotifyItemsChanged
{
    protected readonly MarketRepository Repository;

    private readonly StateFlow<IReadOnlyList<DomainInstrument>> _instruments;
    private readonly List<IDisposable> _subscriptions = [];

    // Per-symbol price cache (key = WatchlistStore.Normalize(symbol)) + the current instrument set, so a
    // membership change can reconcile without re-pricing instruments we already hold. Also guards the
    // in-flight fetch token swap.
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, UiQuote> _priceCache = [];
    private IReadOnlyList<DomainInstrument> _snapshot = [];
    private bool _received; // have we processed the first flow emission yet?
    private int _fetchGeneration; // bumped per fetch so a superseded one's late result is discarded
    private long _lastFullPriceTicks; // Environment.TickCount64 at the last WHOLE-set re-price; 0 = never priced

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Primary flow: drives which rows exist + their pricing (replays the current set at once).
            _subscriptions.Add(_instruments.Subscribe(OnInstrumentsChanged));
            // Secondary flows: a change just re-renders (e.g. the Watchlist's ★ when favorites change).
            foreach (var trigger in RelistTriggers)
                _subscriptions.Add(trigger.Subscribe(_ => RaiseItemsChanged(0)));
            // Live polling: while visible, each tick silently re-prices the set in place. The ticker is a
            // pure event stream (no replay), so opening the page doesn't double-fetch (the membership replay
            // above already did the first load); the ticker runs its timer only while a surface is subscribed.
            _subscriptions.Add(PollTicker.Subscribe(PollRefresh));
            // Re-render when a key is added/cleared so the missing-key hint appears/disappears at once.
            // replay:false — the membership flow above already drives the initial paint.
            _subscriptions.Add(MarketSettingsManager.Instance.HasAnyApiKey
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            // Re-render when throttling starts/stops so the rate-limited banner appears/disappears at once.
            // replay:false — same reason as above.
            _subscriptions.Add(RateLimitSignal.Instance.IsRateLimited
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            Log.Info("Poll", $"{Title}: started polling [{string.Join(", ", _snapshot.Select(i => i.Symbol))}] " +
                             $"(every {MarketSettingsManager.Instance.RefreshMinutes} min, 0=off)");
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            Log.Info("Poll", $"{Title}: stopped polling [{string.Join(", ", _snapshot.Select(i => i.Symbol))}]");
        }
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    protected PricedListPage(MarketRepository repository, StateFlow<IReadOnlyList<DomainInstrument>> instruments)
    {
        Repository = repository;
        _instruments = instruments;
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
        // Demo mode flips the data SOURCE, so every cached price is now wrong. Subscribe for the page's
        // WHOLE life (not the visibility-scoped block above): these pages are long-lived singletons that
        // reconcile against a surviving cache on reopen, so a HIDDEN page must drop its cache on a flip too —
        // otherwise reopening it would repaint stale prices from the old source. replay:false so construction
        // is a no-op (the cache is empty then anyway). Never disposed: the page lives for the whole process.
        _ = MarketSettingsManager.Instance.DemoModeChanged
            .Subscribe(_ => OnDataSourceChanged(), replayOnSubscribe: false);
    }

    // The data source changed (demo mode toggled): the cached prices were produced by the other source and
    // are now wrong. Drop them and reset the freshness clock. If we're visible, re-fetch from the new source
    // right away; if hidden, the cleared cache makes the next open do a full fetch (OnInstrumentsChanged
    // finds everything "missing"), so the flip applies the instant the page is next seen.
    private void OnDataSourceChanged()
    {
        lock (_cacheLock)
        {
            _priceCache.Clear();
            _lastFullPriceTicks = 0;
        }

        if (_itemsChanged is not null) // visible (has subscribers) → reprice now from the new source
            RefreshQuotes();
    }

    // Additional flows whose changes should merely re-render this page (no re-pricing). Default: none.
    // The Watchlist screen overrides this with the Favorites flow so its ★ marks stay current.
    protected virtual IEnumerable<StateFlow<IReadOnlyList<DomainInstrument>>> RelistTriggers => [];

    // Render one priced row.
    protected abstract IListItem BuildRow(UiQuote quote);

    // Shown when the underlying set is empty.
    protected abstract IListItem[] EmptyState();

    // Rows to render ABOVE the priced list — e.g. the Portfolio screen's totals summary. Receives the
    // FULL priced set (NOT filtered by the search box) so a summary always reflects the whole list even
    // while the user filters rows below it. Default: none, so Watchlist/Favorites are unaffected.
    protected virtual IEnumerable<IListItem> LeadingRows(IReadOnlyList<UiQuote> pricedQuotes) => [];

    // Fired after the price cache changes (a fresh fetch landed, or a revisit found it fully cached), on
    // whatever thread did the update, just before the list re-renders. Default: nothing. The Portfolio
    // screen overrides it to (re)fetch FX conversion rates for the currencies now present, then re-render
    // once they land. Kept off the synchronous GetItems path so a keystroke never triggers a network call.
    protected virtual void OnPriceCacheUpdated() { }

    // A snapshot of the currently-priced quotes, in set order (used by OnPriceCacheUpdated overrides to see
    // which currencies need converting). Same projection GetItems renders from.
    protected IReadOnlyList<UiQuote> SnapshotPricedQuotes()
    {
        lock (_cacheLock)
            return [.. _snapshot
                .Select(i => _priceCache.GetValueOrDefault(WatchlistStore.Normalize(i.Symbol)))
                .OfType<UiQuote>()];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (!_received)
            return []; // framework's pre-subscribe fetch — nothing to show yet

        if (_snapshot.Count == 0)
            return ApiKeyHint.StatusRow() is { } emptyHint ? [.. EmptyState(), emptyHint] : EmptyState();

        UiQuote[] cached;
        lock (_cacheLock)
            cached = [.. _snapshot
                .Select(i => _priceCache.GetValueOrDefault(WatchlistStore.Normalize(i.Symbol)))
                .OfType<UiQuote>()];

        if (cached.Length == 0)
            return []; // first prices still loading — let the spinner show

        var rows = new List<IListItem>();
        rows.AddRange(LeadingRows(cached)); // e.g. the Portfolio totals summary — computed from the full set
        rows.AddRange(cached.Where(Matches).Select(BuildRow));
        rows.Add(new ListItem(new RefreshCommand(this)) { Title = Resources.Action_Refresh + " 🔄" });
        rows.Add(AssetIconResolver.AttributionRow()); // Elbstream logo credit (rows above show logos)
        if (ApiKeyHint.StatusRow() is { } hint) // no key → explain blanks; demo mode → flag sample data
            rows.Add(hint);
        if (RateLimitHint.Row() is { } banner) // throttled → surface at the TOP so it's seen without scrolling
            rows.Insert(0, banner);
        return [.. rows];
    }

    // Instant, no-API filter over the already-loaded quotes.
    private bool Matches(UiQuote q) =>
        string.IsNullOrEmpty(SearchText) ||
        q.Symbol.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        q.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    // Reaction to a membership change (and to the initial replay): drop departed rows from the cache for
    // free, then fetch only the symbols we don't yet have.
    private void OnInstrumentsChanged(IReadOnlyList<DomainInstrument> instruments)
    {
        _snapshot = instruments;
        _received = true;

        List<DomainInstrument> missing;
        lock (_cacheLock)
        {
            var keep = instruments.Select(i => WatchlistStore.Normalize(i.Symbol)).ToHashSet();
            foreach (var stale in _priceCache.Keys.Where(k => !keep.Contains(k)).ToList())
                _priceCache.Remove(stale);
            missing = [.. instruments.Where(i => !_priceCache.ContainsKey(WatchlistStore.Normalize(i.Symbol)))];
        }

        if (missing.Count == 0)
        {
            IsLoading = false;
            OnPriceCacheUpdated(); // re-prime conversion rates (e.g. preferred currency may have changed)
            RaiseItemsChanged(0); // removals reflected immediately, no network
            RefreshStaleQuotes(); // ...but if the cached prices have aged past the interval, quietly re-price
            return;
        }

        RaiseItemsChanged(0); // show what's cached now (departed rows already gone) while we fetch
        Fetch(missing);
    }

    // Full re-price of the current set — the manual Refresh row. Clears the cache so every symbol is
    // re-fetched, showing a spinner while it loads.
    internal void RefreshQuotes()
    {
        lock (_cacheLock) _priceCache.Clear();
        Fetch(_snapshot);
    }

    // Live-poll re-price: re-fetch the CURRENT set without clearing the cache or showing a spinner, so the
    // prices on screen stay put and are swapped in place once the new quotes land (no flicker). Reuses the
    // generation guard so a membership change still wins; `silent` also keeps a transient bad poll from
    // blanking a good price (see LoadQuotes).
    internal void PollRefresh()
    {
        var snapshot = _snapshot;
        if (!_received || snapshot.Count == 0)
            return;

        int generation;
        lock (_cacheLock)
            generation = ++_fetchGeneration;

        Log.Info("Poll", $"{Title}: re-pricing [{string.Join(", ", snapshot.Select(i => i.Symbol))}]");
        Task.Run(() => LoadQuotes(snapshot, generation, silent: true));
    }

    // Catch-up for the "short visit" gap in live polling. These page instances are long-lived singletons
    // whose _priceCache survives navigation, so revisiting an UNCHANGED set repaints cached prices and fires
    // NO fetch (nothing missing) — and every revisit restarts the poll timer from a full interval (PollTicker
    // resets the countdown on its 0->1 subscriber transition). So a user whose visits are each shorter than
    // the interval would never see a refreshed price. Fix: when we become visible with a fully-cached set, if
    // those prices have aged past one refresh interval, kick a single SILENT re-price. Bounded to one fetch
    // per revisit and only when actually stale — liveness without burning the Finnhub budget. Gated on
    // AutoRefreshEnabled so "auto-refresh off" (interval 0) stays truly off.
    private void RefreshStaleQuotes()
    {
        var settings = MarketSettingsManager.Instance;
        if (!settings.AutoRefreshEnabled || _lastFullPriceTicks == 0)
            return;

        var age = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastFullPriceTicks);
        if (age < settings.RefreshInterval)
            return;

        Log.Info("Poll", $"{Title}: cached prices aged {age.TotalMinutes:F1} min (>= interval) — silent catch-up re-price");
        PollRefresh();
    }

    // Fetch quotes for the given instruments and merge them into the cache. Tags the fetch with a
    // generation so a full refresh that supersedes it discards this one's late result instead of it
    // clobbering newer data.
    private void Fetch(IReadOnlyList<DomainInstrument> instruments)
    {
        if (instruments.Count == 0)
        {
            IsLoading = false;
            RaiseItemsChanged(0);
            return;
        }

        // A whole-set fetch (initial load or the manual Refresh row) advances the freshness clock; a partial
        // fetch of only newly-added symbols does NOT, so the older rows keep aging toward a catch-up re-price.
        bool wholeSet = instruments.Count == _snapshot.Count;

        int generation;
        lock (_cacheLock)
            generation = ++_fetchGeneration;

        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(() => LoadQuotes(instruments, generation, markFresh: wholeSet));
    }

    // `silent` = a background poll (no spinner): in that mode, don't let a transient bad result (e.g. a
    // Finnhub 429 mapped to an invalid quote) overwrite a price that was fine a moment ago.
    // `markFresh` = this fetch covered the WHOLE current set, so stamp the freshness clock that
    // RefreshStaleQuotes reads (a partial add passes false — see Fetch).
    private async Task LoadQuotes(
        IReadOnlyList<DomainInstrument> instruments, int generation, bool silent = false, bool markFresh = true)
    {
        var quotes = await Repository.GetQuotesAsync(instruments);

        lock (_cacheLock)
        {
            if (generation != _fetchGeneration)
                return; // a newer full refresh took over — drop this stale result

            // Only cache symbols still in the set: a removal during the fetch must not re-add them.
            var keep = _snapshot.Select(i => WatchlistStore.Normalize(i.Symbol)).ToHashSet();
            foreach (var quote in quotes)
            {
                var key = WatchlistStore.Normalize(quote.Symbol);
                if (!keep.Contains(key))
                    continue;

                var ui = UiQuote.From(quote);
                if (silent && !ui.IsValid && _priceCache.TryGetValue(key, out var prev) && prev.IsValid)
                    continue; // keep the last good price through a transient bad poll
                _priceCache[key] = ui;
            }
        }

        if (markFresh)
            _lastFullPriceTicks = Environment.TickCount64; // whole set re-priced — reset the staleness clock

        IsLoading = false;
        OnPriceCacheUpdated(); // prices changed — let a subclass refresh anything derived (e.g. FX rates)
        RaiseItemsChanged(0);
    }

    private sealed partial class RefreshCommand : InvokableCommand
    {
        private readonly PricedListPage _page;

        public RefreshCommand(PricedListPage page)
        {
            _page = page;
            Name = Resources.Action_Refresh;
        }

        public override ICommandResult Invoke()
        {
            _page.RefreshQuotes();
            return CommandResult.KeepOpen();
        }
    }
}

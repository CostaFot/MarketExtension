using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Shared base for the priced list screens (Watchlist, Favorites, Portfolio). Each OBSERVES a different
// membership flow (passed to the ctor — WatchlistStore.Watchlist/Favorites or PortfolioStore.Instruments)
// and renders its instruments priced live.
//
// A PURE OBSERVER of the shared quote cache, like the two dock bands (FavoritesDockPage / PortfolioDockPage):
//
//   * In the INotifyItemsChanged `add` accessor (CmdPal's de-facto "page became visible" hook) the page
//     subscribes to MarketRepository.ObserveQuotes(membership flow) and disposes in `remove`. The
//     membership-aware overload replays the current set's cached quotes on subscribe (the initial paint,
//     fetching only symbols not already cached/stale), re-projects on any membership change (Switch — so a
//     removed row drops and a new one is fetched with no page-side reconcile), and re-emits whenever a member
//     quote changes in the cache (so the repository's single poll loop / demo-flip refill repaint the page).
//   * The page does NOT fetch, poll, or handle demo-mode flips itself — the repository owns all of that, and
//     the cache owns keep-last-good — so these screens can never drift out of sync with each other or the dock.
//   * Delivery is off-thread (ObserveOn in the repository): the emission handler runs on a pool thread with no
//     Rx gate lock held, so RaiseItemsChanged's host call is safe (the deadlock the favorites band hit before
//     the ObserveOn fix). Do NOT add Task.Run / SubscribeOn / ObserveOn on this path.
//
// Typing only re-filters the already-emitted quotes locally — these screens never hit the network on a
// keystroke (only the SearchPage talks to /search, and only on Enter).
internal abstract partial class PricedListPage : DynamicListPage, INotifyItemsChanged
{
    protected readonly MarketRepository Repository;

    private readonly StateFlow<IReadOnlyList<DomainInstrument>> _instruments;
    // Subscriptions in a list (not a single field) so a double-`add` without an intervening `remove` can't
    // orphan a subscription — which would also leave its symbols pinned to the repository's poll loop. Same
    // reasoning and pattern as the dock bands.
    private readonly List<IDisposable> _subscriptions = [];

    // The latest emission projected for rendering, in set order (null entries already dropped by the cache).
    // null until the first emission lands. Written on a pool thread, read on the host thread; reference
    // assignment is atomic and we never mutate the array in place, so no lock (same as the dock bands).
    private UiQuote[]? _quotes;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the cache-backed quote stream for this membership set while the page is visible. Replays
            // current quotes on subscribe (initial paint), re-projects on membership change, re-emits on price
            // change. Project each emission through the async handler with Switch (NOT fire-and-forget): a
            // subclass can await an async projection (the Portfolio screen primes FX rates) before the repaint,
            // and on a cold cache ObserveQuotes emits progressive partial snapshots (CombineLatest fills symbol
            // by symbol), so two awaited handlers could otherwise race and a stale partial finish last. Switch
            // cancels the prior projection's CancellationToken the instant a newer emission arrives, and
            // OnQuotesChangedAsync honors it before painting — so only the latest emission ever reaches the page.
            // (Watchlist/Favorites never await — their projection is a no-op — so this is a no-op for them too.)
            _subscriptions.Add(Repository.ObserveQuotes(_instruments)
                .Select(quotes => Observable.FromAsync(ct => OnQuotesChangedAsync(quotes, ct)))
                .Switch()
                .Subscribe());
            // Secondary flows: a change just re-renders (e.g. the Watchlist's ★ when favorites change).
            foreach (var trigger in RelistTriggers)
                _subscriptions.Add(trigger.Subscribe(_ => RaiseItemsChanged(0)));
            // Re-render when a key is added/cleared so the missing-key hint appears/disappears at once.
            // replay:false — the observe subscription above already drives the initial paint.
            _subscriptions.Add(MarketSettingsManager.Instance.HasAnyApiKey
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            // Re-render when throttling starts/stops so the rate-limited banner appears/disappears at once.
            // replay:false — same reason as above.
            _subscriptions.Add(RateLimitSignal.Instance.IsRateLimited
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            // Spinner only on the very first open of a non-empty set; a reopen keeps the last data showing
            // until the cache replay lands.
            IsLoading = _quotes is null && _instruments.Value.Count > 0;
            Log.Info("Poll", $"{Title}: observing [{string.Join(", ", _instruments.Value.Select(i => i.Symbol))}]");
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            Log.Info("Poll", $"{Title}: stopped observing");
        }
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    protected PricedListPage(MarketRepository repository, StateFlow<IReadOnlyList<DomainInstrument>> instruments)
    {
        Repository = repository;
        _instruments = instruments;
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
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
    // while the user filters rows below it. Default: none, so Watchlist/Favorites are unaffected. Recomputed
    // on every emission (GetItems calls it on each RaiseItemsChanged).
    protected virtual IEnumerable<IListItem> LeadingRows(IReadOnlyList<UiQuote> pricedQuotes) => [];

    // Optional async work a subclass needs done BEFORE an emission is rendered — receives the raw quotes so it
    // can act on their currencies/symbols. The base awaits this, then projects + repaints once. Default:
    // nothing (Watchlist/Favorites). The Portfolio screen overrides it to fetch FX conversion rates so the
    // converted values + total land in the same paint (see PortfolioDockPage for the same pattern). `ct` is
    // cancelled when a newer emission supersedes this one (Switch) — forward it to any async call so a
    // superseded fetch stops early.
    protected virtual Task OnQuotesProjectingAsync(IReadOnlyList<DomainQuote> quotes, CancellationToken ct) => Task.CompletedTask;

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (_quotes is null)
            return []; // before the first cache emission — nothing to show yet (spinner)

        // Empty-vs-loading is read from membership (synchronous, authoritative), not from the emission: the
        // cache emits [] both for an empty set AND for a non-empty set whose prices haven't landed yet.
        if (_instruments.Value.Count == 0)
            return ApiKeyHint.StatusRow() is { } emptyHint ? [.. EmptyState(), emptyHint] : EmptyState();

        var cached = _quotes;
        if (cached.Length == 0)
            return []; // set present, prices still filling the cache — let the spinner show

        var rows = new List<IListItem>();
        rows.AddRange(LeadingRows(cached)); // e.g. the Portfolio totals summary — computed from the full set
        rows.AddRange(cached.Where(Matches).Select(BuildRow));
        rows.Add(new ListItem(new RefreshCommand(this)) { Title = Resources.Action_Refresh + " 🔄" });
        rows.Add(AssetIconResolver.AttributionRow()); // Elbstream logo credit (rows above show logos)
        if (DataSourceAttribution.Row() is { } dataCredit) // Twelve Data (required) / Finnhub data credit
            rows.Add(dataCredit);
        if (ApiKeyHint.StatusRow() is { } hint) // no key → explain blanks; demo mode → flag sample data
            rows.Add(hint);
        if (RateLimitHint.Row() is { } banner) // throttled → surface at the TOP so it's seen without scrolling
            rows.Insert(0, banner);
        return [.. rows];
    }

    // Instant, no-API filter over the already-emitted quotes.
    private bool Matches(UiQuote q) =>
        string.IsNullOrEmpty(SearchText) ||
        q.Symbol.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        q.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    // A new cache emission for the observed set: project it for rendering and repaint. Runs on a pool thread
    // (ObserveQuotes delivers via ObserveOn — no Rx gate lock held), so RaiseItemsChanged's host call is safe
    // and awaiting an async projection here is what makes a subclass's derived data (e.g. the Portfolio screen's
    // converted total) land in a single paint. Driven by Switch (see the subscribe): `ct` is cancelled the
    // moment a newer emission supersedes this one, so it's checked before painting — a stale/partial snapshot
    // bails instead of overwriting _quotes with a less-complete set. Own try/catch so no exception escapes onto
    // the pool thread; OperationCanceledException is the expected supersede path and is swallowed quietly.
    private async Task OnQuotesChangedAsync(IReadOnlyList<DomainQuote> quotes, CancellationToken ct)
    {
        try
        {
            await OnQuotesProjectingAsync(quotes, ct); // Portfolio primes native->preferred FX here; others no-op
            if (ct.IsCancellationRequested) return; // a newer emission supersedes this one — don't paint a stale set
            _quotes = [.. quotes.Select(UiQuote.From)];
            // Spinner while the set is non-empty but its prices haven't filled the cache yet.
            IsLoading = _quotes.Length == 0 && _instruments.Value.Count > 0;
            RaiseItemsChanged(0);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer emission while projecting (e.g. the FX prime was cancelled) — expected, ignore.
        }
        catch (Exception ex)
        {
            Log.Error("Poll", $"{Title}: quote projection failed", ex);
        }
    }

    // The manual Refresh row: force a hard re-fetch of the current set straight through the repository, which
    // writes the fresh prices through the shared cache; the observe subscription then repaints. Because the
    // cache is distinct-until-changed, an UNCHANGED refetch won't re-emit — so this method owns its own
    // spinner (set here, cleared in the finally) to give feedback even when nothing changed. keepLastGood:false
    // makes it a true hard refresh (the old behavior cleared the cache before re-fetching). This runs off the
    // observe-delivery path (a command handler), so a fire-and-forget Task is fine.
    internal void RefreshQuotes()
    {
        var set = _instruments.Value;
        if (set.Count == 0)
            return;

        IsLoading = true;
        RaiseItemsChanged(0);
        _ = Task.Run(async () =>
        {
            try { await Repository.RefreshAsync(set, keepLastGood: false); }
            catch (Exception ex) { Log.Error("Poll", $"{Title}: manual refresh failed", ex); }
            finally
            {
                IsLoading = false;
                RaiseItemsChanged(0);
            }
        });
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

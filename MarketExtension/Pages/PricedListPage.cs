using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

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
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    protected PricedListPage(MarketRepository repository, StateFlow<IReadOnlyList<DomainInstrument>> instruments)
    {
        Repository = repository;
        _instruments = instruments;
        Icon = new IconInfo("https://github.com/favicon.ico");
    }

    // Additional flows whose changes should merely re-render this page (no re-pricing). Default: none.
    // The Watchlist screen overrides this with the Favorites flow so its ★ marks stay current.
    protected virtual IEnumerable<StateFlow<IReadOnlyList<DomainInstrument>>> RelistTriggers => [];

    // Render one priced row.
    protected abstract IListItem BuildRow(UiQuote quote);

    // Shown when the underlying set is empty.
    protected abstract IListItem[] EmptyState();

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (!_received)
            return []; // framework's pre-subscribe fetch — nothing to show yet

        if (_snapshot.Count == 0)
            return EmptyState();

        UiQuote[] cached;
        lock (_cacheLock)
            cached = [.. _snapshot
                .Select(i => _priceCache.GetValueOrDefault(WatchlistStore.Normalize(i.Symbol)))
                .OfType<UiQuote>()];

        if (cached.Length == 0)
            return []; // first prices still loading — let the spinner show

        var rows = cached.Where(Matches).Select(BuildRow).ToList();
        rows.Add(new ListItem(new RefreshCommand(this)) { Title = "Refresh 🔄" });
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
            RaiseItemsChanged(0); // removals reflected immediately, no network
            return;
        }

        RaiseItemsChanged(0); // show what's cached now (departed rows already gone) while we fetch
        Fetch(missing);
    }

    // Full re-price of the current set — the Refresh row and (later) the poll timer. Clears the cache so
    // every symbol is re-fetched.
    internal void RefreshQuotes()
    {
        lock (_cacheLock) _priceCache.Clear();
        Fetch(_snapshot);
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

        int generation;
        lock (_cacheLock)
            generation = ++_fetchGeneration;

        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(() => LoadQuotes(instruments, generation));
    }

    private async Task LoadQuotes(IReadOnlyList<DomainInstrument> instruments, int generation)
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
                if (keep.Contains(key))
                    _priceCache[key] = UiQuote.From(quote);
            }
        }

        IsLoading = false;
        RaiseItemsChanged(0);
    }

    private sealed partial class RefreshCommand : InvokableCommand
    {
        private readonly PricedListPage _page;

        public RefreshCommand(PricedListPage page)
        {
            _page = page;
            Name = "Refresh";
        }

        public override ICommandResult Invoke()
        {
            _page.RefreshQuotes();
            return CommandResult.KeepOpen();
        }
    }
}

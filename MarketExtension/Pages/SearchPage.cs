using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Default entry screen: the Enter-only Finnhub /search flow. Typing never hits the network
// (free-tier rate limits) — it only changes what the synthetic "Search Finnhub for ..." item will
// look up. Results are price-less identities; Enter on a result opens its SymbolDetailPage, which is
// the single place to add it to the watchlist or favorites. The subtitle still reflects current
// membership so the user can see at a glance what a result is already on.
// Lifted from the search half of the old MarketsPage.
//
// Subscribes to the WatchlistStore membership flows while visible (the INotifyItemsChanged add/remove
// lifecycle) so a row's membership subtitle/★ refreshes the instant it's added — no manual callback.
// Their replay-on-subscribe also paints the initial list; the async /search completion re-lists too.
internal sealed partial class SearchPage : DynamicListPage, INotifyItemsChanged
{
    private const string SearchGlyph = "\uE721"; // Segoe MDL2 Search
    private const string ListGlyph = "\uE8FD"; // Segoe MDL2 List
    private const string StarFillGlyph = "\uE735"; // Segoe MDL2 FavoriteStarFill

    private readonly MarketRepository _repository;
    private readonly WatchlistPage _watchlistPage;
    private readonly FavoritesPage _favoritesPage;

    // Last executed online search and the exact query it belongs to. Results render only while
    // SearchText still equals _searchedQuery, so they vanish as the user edits the query and reappear
    // (without a new call) if they type it back.
    private IReadOnlyList<DomainInstrument>? _searchResults;
    private string _searchedQuery = string.Empty;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private readonly List<IDisposable> _subscriptions = [];

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Results are identity-only (no prices), but each row's subtitle/★ reflects current
            // membership — so re-render whenever the watchlist or favorites change (e.g. right after an
            // Enter/Ctrl+Enter add). StateFlow's replay also fires the initial list on subscribe, which
            // replaces the old explicit fire-on-subscribe.
            _subscriptions.Add(WatchlistStore.Instance.Watchlist.Subscribe(_ => RaiseItemsChanged(0)));
            _subscriptions.Add(WatchlistStore.Instance.Favorites.Subscribe(_ => RaiseItemsChanged(0)));
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

    public SearchPage(MarketRepository repository)
    {
        _repository = repository;
        _watchlistPage = new WatchlistPage(repository);
        _favoritesPage = new FavoritesPage(repository);
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = "Markets Search";
        Name = "Search";
        PlaceholderText = "Type a symbol or name and press Enter to search...";
    }

    // Typing only re-lists — never calls the API. The online search is Enter-only.
    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
        => string.IsNullOrWhiteSpace(SearchText) ? HomeItems() : SearchItems();

    // Empty box: quick links into the other two screens (also top-level commands).
    private IListItem[] HomeItems() =>
    [
        new ListItem(_watchlistPage)
        {
            Title = "Watchlist",
            Subtitle = "The instruments you track",
            Icon = new IconInfo(ListGlyph),
        },
        new ListItem(_favoritesPage)
        {
            Title = "Favorites",
            Subtitle = "Your starred instruments — shown on the dock",
            Icon = new IconInfo(StarFillGlyph),
        },
    ];

    // Non-empty box: the Enter-to-search action, then the last online search's results (if they
    // still belong to the current query).
    private IListItem[] SearchItems()
    {
        var items = new List<IListItem>
        {
            // First item → pressing Enter runs the one /search call (see RunSearchCommand).
            new ListItem(new RunSearchCommand(this))
            {
                Title = $"Search Finnhub for \"{SearchText}\"",
                Subtitle = "Press Enter to look up symbols online",
                Icon = new IconInfo(SearchGlyph),
            },
        };

        if (_searchResults is not null &&
            string.Equals(_searchedQuery, SearchText, StringComparison.OrdinalIgnoreCase))
        {
            if (_searchResults.Count == 0)
            {
                items.Add(new ListItem(new NoOpCommand())
                {
                    Title = $"No matches for \"{SearchText}\"",
                    Section = "Search results",
                });
            }

            foreach (var instrument in _searchResults)
                items.Add(BuildResultItem(instrument));
        }

        return [.. items];
    }

    // A price-less search result. Enter opens its detail page (the single place to add it to the
    // watchlist or favorites). The subtitle reflects current membership so the user can see at a
    // glance what the row is already on.
    private ListItem BuildResultItem(DomainInstrument instrument)
    {
        var inWatchlist = WatchlistStore.Instance.IsInWatchlist(instrument.Symbol);
        var isFavorite = WatchlistStore.Instance.IsFavorite(instrument.Symbol);

        return new ListItem(new SymbolDetailPage(instrument, _repository))
        {
            Title = (isFavorite ? "★ " : "") + $"{instrument.Symbol} · {instrument.Name}",
            Subtitle = MembershipSubtitle(inWatchlist, isFavorite),
            Section = "Search results",
        };
    }

    private static string MembershipSubtitle(bool inWatchlist, bool isFavorite) => (inWatchlist, isFavorite) switch
    {
        (true, true) => "On watchlist · ★ Favorite · Enter for details",
        (true, false) => "On watchlist · Enter for details",
        (false, true) => "★ Favorite · Enter for details",
        (false, false) => "Enter for details",
    };

    // Runs the single online /search call for the current SearchText, then re-lists with results.
    internal void RunSearch()
    {
        var query = SearchText;
        if (string.IsNullOrWhiteSpace(query))
            return;

        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(() => SearchOnline(query));
    }

    private async Task SearchOnline(string query)
    {
        var results = await _repository.SearchAsync(query);
        _searchResults = results;
        _searchedQuery = query;
        IsLoading = false;
        RaiseItemsChanged(0);
    }

    private sealed partial class RunSearchCommand : InvokableCommand
    {
        private readonly SearchPage _page;

        public RunSearchCommand(SearchPage page)
        {
            _page = page;
            Name = "Search";
            Icon = new IconInfo(SearchGlyph);
        }

        public override ICommandResult Invoke()
        {
            _page.RunSearch();
            return CommandResult.KeepOpen();
        }
    }
}

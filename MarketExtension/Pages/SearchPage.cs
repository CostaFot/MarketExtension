using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Default entry screen: the Enter-only Finnhub /search flow. Typing never hits the network
// (free-tier rate limits) — it only changes what the synthetic "Search Finnhub for ..." item will
// look up. Results are price-less identities; Enter adds the instrument to the watchlist, Ctrl+Enter
// (the first MoreCommands context item) adds it to favorites. The two lists are independent — a
// result can go on either, both, or neither. Lifted from the search half of the old MarketsPage.
//
// Uses the project's INotifyItemsChanged fire-on-subscribe hook (see CLAUDE.md) so the async
// /search completion and the post-add re-lists reach the framework's listener.
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

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(-1)); }
        remove => _itemsChanged -= value;
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

    // A price-less search result. Enter adds to the watchlist; Ctrl+Enter adds to favorites. The
    // subtitle reflects current membership so the available actions are obvious.
    private ListItem BuildResultItem(DomainInstrument instrument)
    {
        var inWatchlist = WatchlistStore.Instance.IsInWatchlist(instrument.Symbol);
        var isFavorite = WatchlistStore.Instance.IsFavorite(instrument.Symbol);

        return new ListItem(new AddToWatchlistCommand(instrument, () => RaiseItemsChanged(0)))
        {
            Title = (isFavorite ? "★ " : "") + $"{instrument.Symbol} · {instrument.Name}",
            Subtitle = MembershipSubtitle(inWatchlist, isFavorite),
            Section = "Search results",
            MoreCommands =
            [
                new CommandContextItem(new AddToFavoritesCommand(instrument, () => RaiseItemsChanged(0)))
                {
                    Title = "Add to Favorites",
                },
            ],
        };
    }

    private static string MembershipSubtitle(bool inWatchlist, bool isFavorite) => (inWatchlist, isFavorite) switch
    {
        (true, true) => "On watchlist · ★ Favorite",
        (true, false) => "On watchlist · Ctrl+Enter to favorite",
        (false, true) => "★ Favorite · Enter to add to watchlist",
        (false, false) => "Enter to add to watchlist · Ctrl+Enter to favorite",
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

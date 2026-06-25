using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Default entry screen: the Enter-only online symbol-search flow. Typing never hits the network
// (free-tier rate limits) — it only changes what the synthetic "Search markets for ..." item will
// look up. The query fans out to every provider via MarketRepository.SearchAsync (provider-agnostic).
// Results are price-less identities; Enter on a result opens its SymbolDetailPage, which is
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
            // Re-render when a key is added/cleared so the missing-key hint appears/disappears at once.
            // replay:false — the membership replay above already paints the initial list.
            _subscriptions.Add(MarketSettingsManager.Instance.HasAnyApiKey
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            // Re-render when demo mode toggles so the status row (demo vs missing-key) flips at once. The next
            // /search already routes to the new source; this just keeps the row in sync without a re-search.
            _subscriptions.Add(MarketSettingsManager.Instance.DemoModeChanged
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            // Re-render when throttling starts/stops so the rate-limited banner appears/disappears at once.
            _subscriptions.Add(RateLimitSignal.Instance.IsRateLimited
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public SearchPage(MarketRepository repository)
    {
        _repository = repository;
        _watchlistPage = new WatchlistPage(repository);
        _favoritesPage = new FavoritesPage(repository);
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
        Title = Resources.Page_Search_Title;
        Name = Resources.Action_Search;
        PlaceholderText = Resources.Search_Placeholder;
    }

    // Typing only re-lists — never calls the API. The online search is Enter-only.
    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
        => string.IsNullOrWhiteSpace(SearchText) ? HomeItems() : SearchItems();

    // Empty box: quick links into the other two screens (also top-level commands).
    private IListItem[] HomeItems()
    {
        var items = new List<IListItem>
        {
            new ListItem(_watchlistPage)
            {
                Title = Resources.Nav_Watchlist_Title,
                Subtitle = Resources.Nav_Watchlist_Subtitle,
                Icon = new IconInfo(ListGlyph),
            },
            new ListItem(_favoritesPage)
            {
                Title = Resources.Nav_Favorites_Title,
                Subtitle = Resources.Nav_Favorites_Subtitle,
                Icon = new IconInfo(StarFillGlyph),
            },
        };

        if (ApiKeyHint.StatusRow() is { } hint) // no key → nudge to add one; demo mode → flag sample data
            items.Add(hint);

        return [.. items];
    }

    // Non-empty box: the Enter-to-search action, then the last online search's results (if they
    // still belong to the current query).
    private IListItem[] SearchItems()
    {
        var items = new List<IListItem>
        {
            // First item → pressing Enter runs the one /search call (see RunSearchCommand).
            new ListItem(new RunSearchCommand(this))
            {
                Title = Strings.Format(Resources.Search_Action_Title, SearchText),
                Subtitle = Resources.Search_Action_Subtitle,
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
                    Title = Strings.Format(Resources.Search_NoMatches, SearchText),
                    Section = Resources.Search_ResultsSection,
                });
            }

            foreach (var instrument in _searchResults)
                items.Add(BuildResultItem(instrument));

            if (_searchResults.Count > 0)
                items.Add(AssetIconResolver.AttributionRow()); // Elbstream logo credit (results show logos)
        }

        if (ApiKeyHint.StatusRow() is { } hint) // no key → explain empty search; demo mode → flag sample data
            items.Add(hint);
        if (RateLimitHint.Row() is { } banner) // throttled → just under the search action, seen without scrolling
            items.Insert(1, banner);

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
            Section = Resources.Search_ResultsSection,
            Icon = AssetIconResolver.Resolve(instrument),
        };
    }

    private static string MembershipSubtitle(bool inWatchlist, bool isFavorite) => (inWatchlist, isFavorite) switch
    {
        (true, true) => Resources.Membership_WatchlistFavorite,
        (true, false) => Resources.Membership_Watchlist,
        (false, true) => Resources.Membership_Favorite,
        (false, false) => Resources.Membership_None,
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
            Name = Resources.Action_Search;
            Icon = new IconInfo(SearchGlyph);
        }

        public override ICommandResult Invoke()
        {
            _page.RunSearch();
            return CommandResult.KeepOpen();
        }
    }
}

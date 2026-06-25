using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// The single top-level "Markets" command: a hub that funnels into the three market screens
// (Search / Watchlist / Favorites). Replaces the old three separate top-level commands, so the
// Command Palette root now carries one "Markets" entry instead of polluting it with three. Each row
// navigates into the existing page unchanged ("everything is the same after that"). The sub-pages are
// built once and reused, so their per-page price caches survive navigating in and out of the hub.
internal sealed partial class MarketsPage : ListPage, INotifyItemsChanged
{
    private const string SearchGlyph = "\uE721";    // Segoe MDL2 Search
    private const string ListGlyph = "\uE8FD";      // Segoe MDL2 List
    private const string StarFillGlyph = "\uE735";  // Segoe MDL2 FavoriteStarFill
    private const string PortfolioGlyph = "\uE825"; // Segoe MDL2 Bank
    private const string InfoGlyph = "\uE946";      // Segoe MDL2 Info
    private const string SettingsGlyph = "\uE713";  // Segoe MDL2 Setting

    private readonly SearchPage _searchPage;
    private readonly WatchlistPage _watchlistPage;
    private readonly FavoritesPage _favoritesPage;
    private readonly PortfolioPage _portfolioPage;
    private readonly DataSourcesPage _dataSourcesPage;
    private readonly IContentPage _settingsPage;

    // The hub is otherwise a static list, but the missing-key hint row must react when a key is
    // added/cleared. Re-implement INotifyItemsChanged so we can subscribe to the key flow while visible
    // (the add/remove accessors are CmdPal's de-facto Loaded/Unloaded hooks) and re-list on a flip.
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private readonly List<IDisposable> _subscriptions = [];

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // replay:false \u2014 the framework's initial fetch already painted the rows (incl. the hint);
            // we only need to re-list when the key presence actually flips.
            _subscriptions.Add(MarketSettingsManager.Instance.HasAnyApiKey
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            // Same for demo mode: re-list so the status row swaps to/from the blue "Demo mode" row at once.
            _subscriptions.Add(MarketSettingsManager.Instance.DemoModeChanged
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

    public MarketsPage(MarketRepository repository)
    {
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
        Title = "Markets";
        Name = "Open";
        PlaceholderText = "Search, Watchlist, Favorites...";

        _searchPage = new SearchPage(repository);
        _watchlistPage = new WatchlistPage(repository);
        _favoritesPage = new FavoritesPage(repository);
        _portfolioPage = new PortfolioPage(repository);
        _dataSourcesPage = new DataSourcesPage();

        // The toolkit builds a navigable settings page straight from our settings (Finnhub API key +
        // refresh interval) \u2014 the same form Command Palette shows in its own Settings UI.
        _settingsPage = MarketSettingsManager.Instance.Settings.SettingsPage;
    }

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>
        {
            new ListItem(_searchPage)
            {
                Title = "Search",
                Subtitle = "Look up any stock, crypto, or currency",
                Icon = new IconInfo(SearchGlyph),
            },
            new ListItem(_watchlistPage)
            {
                Title = "Watchlist",
                Subtitle = "The instruments you track, priced live",
                Icon = new IconInfo(ListGlyph),
            },
            new ListItem(_favoritesPage)
            {
                Title = "Favorites",
                Subtitle = "Your starred instruments — shown on the dock",
                Icon = new IconInfo(StarFillGlyph),
            },
            new ListItem(_portfolioPage)
            {
                Title = "Portfolio",
                Subtitle = "Your holdings, total value, and daily P&L",
                Icon = new IconInfo(PortfolioGlyph),
            },
            new ListItem(_dataSourcesPage)
            {
                Title = "Data Sources",
                Subtitle = "Where quotes come from and how your keys are used",
                Icon = new IconInfo(InfoGlyph),
            },
            new ListItem(_settingsPage)
            {
                Title = "Settings",
                Subtitle = "API keys and price refresh interval",
                Icon = new IconInfo(SettingsGlyph),
            },
        };

        // Surface the data state up front on the hub: no key → most of the app shows nothing; demo mode →
        // the data is sample/simulated.
        if (ApiKeyHint.StatusRow() is { } hint)
            items.Add(hint);

        return [.. items];
    }
}

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// The single top-level "Markets" command: a hub that funnels into the three market screens
// (Search / Watchlist / Favorites). Replaces the old three separate top-level commands, so the
// Command Palette root now carries one "Markets" entry instead of polluting it with three. Each row
// navigates into the existing page unchanged ("everything is the same after that"). The sub-pages are
// built once and reused, so their per-page price caches survive navigating in and out of the hub.
internal sealed partial class MarketsPage : ListPage
{
    private const string SearchGlyph = "\uE721";   // Segoe MDL2 Search
    private const string ListGlyph = "\uE8FD";     // Segoe MDL2 List
    private const string StarFillGlyph = "\uE735"; // Segoe MDL2 FavoriteStarFill

    private readonly SearchPage _searchPage;
    private readonly WatchlistPage _watchlistPage;
    private readonly FavoritesPage _favoritesPage;

    public MarketsPage(MarketRepository repository)
    {
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = "Markets";
        Name = "Open";
        PlaceholderText = "Search, Watchlist, Favorites...";

        _searchPage = new SearchPage(repository);
        _watchlistPage = new WatchlistPage(repository);
        _favoritesPage = new FavoritesPage(repository);
    }

    public override IListItem[] GetItems() =>
    [
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
    ];
}

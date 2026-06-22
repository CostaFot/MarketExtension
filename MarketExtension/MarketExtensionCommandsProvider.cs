using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

public partial class MarketExtensionCommandsProvider : CommandProvider
{
    // The repository coordinates all market-data providers; both the palette page and the dock
    // band share this one instance. Add a provider here to extend coverage (e.g. a forex source);
    // MockMarketDataProvider is the offline fallback.
    private readonly MarketRepository _repository = new(new FinnhubMarketDataProvider());

    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;

    public MarketExtensionCommandsProvider()
    {
        Id = "com.costafotiadis.market";
        DisplayName = "Market Extension";
        Icon = new IconInfo("https://github.com/favicon.ico");

        // Surface the extension's settings (Finnhub API key + price refresh interval) in the
        // Command Palette Settings UI. See Settings/MarketSettingsManager.cs.
        Settings = MarketSettingsManager.Instance.Settings;

        // Three top-level screens: search (the default entry), the tracked watchlist, and the
        // curated favorites. All share the one repository; favorites also feed the dock band below.
        // All three titles share the "Markets " prefix so they group together (and don't pollute the
        // global namespace) when the user searches the Command Palette root.
        _commands = [
            new CommandItem(new SearchPage(_repository)) { Title = "Markets Search" },
            new CommandItem(new WatchlistPage(_repository)) { Title = "Markets Watchlist" },
            new CommandItem(new FavoritesPage(_repository)) { Title = "Markets Favorites" },
        ];

        // Dock band: a ticker strip of up to 3 favorited instruments. Pinned from the Dock.
        _dockBands = [
            new CommandItem(new FavoritesDockPage(_repository)) { Title = "Markets" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;
}

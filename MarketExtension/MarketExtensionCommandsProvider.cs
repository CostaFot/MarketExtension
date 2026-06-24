using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

public partial class MarketExtensionCommandsProvider : CommandProvider
{
    // The repository coordinates all market-data providers; both the palette page and the dock
    // band share this one instance. Routing is by asset class: Finnhub serves stocks + crypto,
    // Frankfurter serves forex (keyless ECB rates). Add a provider here to extend coverage;
    // MockMarketDataProvider is the offline fallback.
    private readonly MarketRepository _repository =
        new(new FinnhubMarketDataProvider(), new FrankfurterMarketDataProvider());

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

        // A single top-level "Markets" command that opens the MarketsPage hub. From there the user
        // funnels into the three screens — Search, Watchlist, Favorites — which are otherwise
        // unchanged. This keeps the Command Palette root to one entry instead of three. Favorites
        // also feed the dock band below.
        _commands = [
            new CommandItem(new MarketsPage(_repository)) { Title = "Markets" },
        ];

        // Dock band: a ticker strip of up to 3 favorited instruments. Pinned from the Dock.
        _dockBands = [
            new CommandItem(new FavoritesDockPage(_repository)) { Title = "Markets" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;
}

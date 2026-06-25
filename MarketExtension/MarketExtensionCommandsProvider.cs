using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

public partial class MarketExtensionCommandsProvider : CommandProvider
{
    // The repository coordinates all market-data providers; both the palette page and the dock
    // band share this one instance. Routing is by asset class, first-match in this order: Twelve Data
    // (stocks + crypto + forex, plus free price charts) is primary WHEN its key is set — its Supports()
    // is gated on the key, so with no Twelve Data key the routing falls through to Finnhub (stocks +
    // crypto) and Frankfurter (forex, keyless ECB rates). Add a provider here to extend coverage;
    // MockMarketDataProvider is the offline fallback.
    private readonly MarketRepository _repository =
        new(new TwelveDataMarketDataProvider(),
            new FinnhubMarketDataProvider(),
            new FrankfurterMarketDataProvider());

    private readonly ICommandItem[] _commands;
    private readonly ICommandItem[] _dockBands;

    public MarketExtensionCommandsProvider()
    {
        Id = "com.costafotiadis.market";
        DisplayName = "Market Extension";
        Icon = new IconInfo("https://github.com/favicon.ico");

        // Surface the extension's settings (Twelve Data + Finnhub API keys + price refresh interval)
        // in the Command Palette Settings UI. See Settings/MarketSettingsManager.cs.
        Settings = MarketSettingsManager.Instance.Settings;

        // A single top-level "Markets" command that opens the MarketsPage hub. From there the user
        // funnels into the three screens — Search, Watchlist, Favorites — which are otherwise
        // unchanged. This keeps the Command Palette root to one entry instead of three. Favorites
        // also feed the dock band below.
        _commands = [
            new CommandItem(new MarketsPage(_repository)) { Title = "Markets" },
        ];

        // Dock bands, each pinnable from the Dock: a ticker strip of favorited instruments, and a
        // one-line portfolio total (value + daily P&L in the preferred currency).
        _dockBands = [
            new CommandItem(new FavoritesDockPage(_repository)) { Title = "Markets" },
            new CommandItem(new PortfolioDockPage(_repository)) { Title = "Markets Portfolio" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;
}

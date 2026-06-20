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

        // To expose extension settings, add a JsonSettingsManager and assign:
        //   Settings = MySettingsManager.Instance.Settings;
        // See reference/settings/AdbSettingsManager.cs for an example.

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

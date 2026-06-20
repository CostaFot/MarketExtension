using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

public partial class MarketExtensionCommandsProvider : CommandProvider
{
    // One shared data source for both the palette page and the dock band. Swap this single
    // line for a real API-backed IMarketDataProvider later (see reference/dock-support.md).
    private readonly IMarketDataProvider _dataProvider = new MockMarketDataProvider();

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
            new CommandItem(new MarketsPage(_dataProvider)) { Title = "Markets" },
        ];

        // Dock band: a ticker strip of up to 3 favorited instruments. Pinned from the Dock.
        _dockBands = [
            new CommandItem(new FavoritesDockPage(_dataProvider)) { Title = "Markets" },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override ICommandItem[]? GetDockBands() => _dockBands;
}

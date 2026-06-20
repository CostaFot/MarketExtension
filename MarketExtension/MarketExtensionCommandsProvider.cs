using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

public partial class MarketExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;

    public MarketExtensionCommandsProvider()
    {
        DisplayName = "Market Extension";
        Icon = new IconInfo("https://github.com/favicon.ico");

        // To expose extension settings, add a JsonSettingsManager and assign:
        //   Settings = MySettingsManager.Instance.Settings;
        // See reference/settings/AdbSettingsManager.cs for an example.

        _commands = [
            new CommandItem(new MarketsPage()) { Title = "Markets" },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }
}

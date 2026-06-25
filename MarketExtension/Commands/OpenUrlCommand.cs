using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Opens a URL in the user's default browser. Used by the Elbstream attribution row (see
// AssetIconResolver.AttributionRow). Keeps the palette open so the user returns to their list after the
// browser launches.
internal sealed partial class OpenUrlCommand : InvokableCommand
{
    private readonly string _url;

    public OpenUrlCommand(string url, string name)
    {
        _url = url;
        Name = name;
    }

    public override ICommandResult Invoke()
    {
        try
        {
            ProcessHelper.OpenUrl(_url);
            return CommandResult.KeepOpen();
        }
        catch (Exception ex)
        {
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = Strings.Format("Toast_OpenUrlFailed", _url, ex.Message),
                Result = CommandResult.KeepOpen(),
            });
        }
    }
}

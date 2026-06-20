using System;
using System.ComponentModel;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Sample InvokableCommand showing the success/error toast convention used across the
// extension: success dismisses, errors KeepOpen() so the user can read them, and a
// missing external tool (Win32 errno 2) gets a friendly message. Replace the body with
// real work. See reference/commands/ for richer examples (process exec, toggles,
// confirmation dialogs, multi-step flows).
internal sealed partial class SampleCommand : InvokableCommand
{
    public SampleCommand()
    {
        Name = "Run Sample";
        Icon = new IconInfo(""); // Lightning
    }

    public override ICommandResult Invoke()
    {
        try
        {
            // do work here — e.g. ProcessHelper.Run("some.exe", "args", out var stdout, out var stderr);
            return CommandResult.ShowToast("Sample command ran.");
        }
        catch (Exception ex) when (ex is Win32Exception w && w.NativeErrorCode == 2)
        {
            return ErrorToast("Required tool not found. Make sure it is installed and on your PATH.");
        }
        catch (Exception ex)
        {
            return ErrorToast($"Unexpected error: {ex.Message}");
        }
    }

    private static ICommandResult ErrorToast(string message) =>
        CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
}

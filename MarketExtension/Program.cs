using Microsoft.CommandPalette.Extensions;
using Sentry;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarketExtension;

public class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [MTAThread]
    public static void Main(string[] args)
    {
        // Optional crash/usage telemetry. Leave the DSN empty to disable Sentry, or set
        // your own DSN (do NOT reuse another project's DSN). Remove this block entirely
        // if you don't want Sentry.
        using var _ = SentrySdk.Init(options =>
        {
            options.Dsn = ""; // <-- your Sentry DSN here (empty = disabled)
            options.AutoSessionTracking = true;
        });

        Log.Info("Startup", "MarketExtension starting");

        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            Log.Info("ComServer", "RegisterProcessAsComServer mode detected");
            try
            {
                global::Shmuelie.WinRTServer.ComServer server = new();
                ManualResetEvent extensionDisposedEvent = new(false);
                MarketExtension extensionInstance = new(extensionDisposedEvent);
                server.RegisterClass<MarketExtension, IExtension>(() => extensionInstance);
                Log.Info("ComServer", "COM server registered, starting...");
                server.Start();
                Log.Info("ComServer", "COM server started, waiting for disposal signal");
                extensionDisposedEvent.WaitOne();
                Log.Info("ComServer", "Disposal signal received, stopping server");
                server.Stop();
                server.UnsafeDispose();
            }
            catch (Exception ex)
            {
                Log.Error("ComServer", "COM server failed", ex);
            }
        }
        else
        {
            MessageBox(
                IntPtr.Zero,
                "Market Extension is a background extension.\n\nTo use it, open PowerToys Command Palette and search for \"Market\".",
                "Market Extension",
                0x40 /* MB_ICONINFORMATION */);
        }
    }
}

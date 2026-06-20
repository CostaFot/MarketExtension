using System;
using System.Diagnostics;

namespace MarketExtension;

// Lightweight tagged logger — writes to the Trace stream only (no files). That means
// OutputDebugString, which you can watch live with Sysinternals DebugView (run as admin →
// Capture → "Capture Global Win32"), an attached debugger, or the VS Output window.
//
// Format: "[time] [LEVEL] [Tag] message". The <tag> is a component name (e.g. "Finnhub",
// "Repository", "ComServer"). NEVER pass secrets (API tokens) into a message.
internal static class Log
{
    public static void Info(string tag, string message) => Write("INFO", tag, message);

    public static void Warn(string tag, string message) => Write("WARN", tag, message);

    public static void Error(string tag, string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
        Write("ERROR", tag, text);
    }

    private static void Write(string level, string tag, string message) =>
        Trace.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{tag}] {message}");
}

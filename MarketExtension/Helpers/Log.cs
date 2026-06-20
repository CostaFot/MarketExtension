using System;
using System.Diagnostics;
using Sentry;

namespace MarketExtension;

// Lightweight tagged logger.
//
// Info/Warn write to the Debug stream (OutputDebugString) — watch them live in the VS Output
// window or Sysinternals DebugView. They are marked [Conditional("DEBUG")], so in a Release build
// the calls (and their argument evaluation) are compiled away entirely — a shipped MSIX emits
// nothing here: no overhead, and nothing for a local DebugView to capture.
//
// Error is NOT conditional: its Debug line is still stripped in Release, but it additionally
// reports to Sentry, which is the production diagnostics channel (a no-op until a DSN is set in
// Program.cs). Format: "[time] [LEVEL] [Tag] message". The <tag> is a component name (e.g.
// "Finnhub", "Repository"). NEVER pass secrets (API tokens) into a message.
internal static class Log
{
    [Conditional("DEBUG")]
    public static void Info(string tag, string message) => Write("INFO", tag, message);

    [Conditional("DEBUG")]
    public static void Warn(string tag, string message) => Write("WARN", tag, message);

    public static void Error(string tag, string message, Exception? ex = null)
    {
        // Debug-only line (stripped in Release).
        Write("ERROR", tag, ex is null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        // Production error channel — survives into Release (no-op while the Sentry DSN is empty).
        if (ex is not null)
            SentrySdk.CaptureException(ex);
        else
            SentrySdk.CaptureMessage($"[{tag}] {message}", SentryLevel.Error);
    }

    [Conditional("DEBUG")]
    private static void Write(string level, string tag, string message) =>
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{tag}] {message}");
}

using System.Diagnostics;

namespace MarketExtension;

internal static class ProcessHelper
{
    // Runs an external process, reading BOTH stdout and stderr before WaitForExit to
    // avoid pipe-buffer deadlocks (the buffers can fill and block the child process if
    // you wait first, then read). stderr is returned only when the process signals
    // failure (non-zero exit); otherwise it is empty.
    //
    // This is the genericized version of the AdbExtension's AdbHelper.RunAdb — see
    // reference/helpers/AdbHelper.cs for the original (which also parsed adb output).
    public static void Run(string fileName, string arguments, out string stdout, out string stderr)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        process.Start();
        string outText = process.StandardOutput.ReadToEnd();
        string errText = process.StandardError.ReadToEnd();
        process.WaitForExit();

        stdout = outText;
        stderr = process.ExitCode != 0
            ? (string.IsNullOrWhiteSpace(errText) ? $"{fileName} exited with code {process.ExitCode}" : errText.Trim())
            : string.Empty;
    }

    // Opens a URL (or file/protocol) with the OS default handler. Unlike Run, this is a fire-and-forget
    // shell launch — no output is captured (UseShellExecute can't redirect streams).
    public static void OpenUrl(string url)
        => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

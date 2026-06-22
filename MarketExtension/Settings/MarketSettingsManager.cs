using System;
using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Extension settings, surfaced in Command Palette's Settings UI and persisted to
// market.settings.json under the CmdPal settings folder. Singleton, modeled on
// reference/settings/AdbSettingsManager.cs. Wired into the host via
// MarketExtensionCommandsProvider (Settings = MarketSettingsManager.Instance.Settings).
//
// Settings today:
//   - Finnhub API key. This is the ONLY source of the key — there is no built-in/baked key; an
//     empty value means the Finnhub provider returns no prices until the user supplies their own.
//   - Price refresh interval in minutes (the seam for the upcoming live-price polling; 0 = off).
//
// Naming note: provider keys are named per-provider (FinnhubApiKey, not a generic ApiKey) because
// additional providers (e.g. a forex source) will each get their own key setting here later.
//
// Note: the API key is NOT masked — the toolkit's TextSetting has no password mode. That's
// acceptable here: settings live in a local plaintext JSON file anyway.
internal sealed class MarketSettingsManager : JsonSettingsManager
{
    public static readonly MarketSettingsManager Instance = new();

    // Default auto-refresh cadence when the field is blank/unparseable.
    private const int DefaultRefreshMinutes = 5;

    private readonly TextSetting _finnhubApiKey = new("finnhubApiKey", string.Empty)
    {
        Label = "Finnhub API key",
        Description = "Your Finnhub API key. Required — prices won't load until this is set. " +
                      "Get a free key at https://finnhub.io.",
        Placeholder = "Paste your Finnhub API key",
    };

    private readonly TextSetting _refreshMinutes = new(
        "refreshMinutes", DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture))
    {
        Label = "Price refresh interval (minutes)",
        Description = "How often to refresh live prices while a Markets screen is open. " +
                      "Enter 0 to turn auto-refresh off. Lower values use more of the Finnhub rate limit.",
        Placeholder = DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture),
    };

    // The key the Finnhub provider should use — the user's setting, or empty when unset (there is no
    // built-in fallback). Read as a property (not cached) so a key change in settings applies on the
    // next request without a reload.
    public string FinnhubApiKey => _finnhubApiKey.Value?.Trim() ?? string.Empty;

    // Whether a Finnhub key has been configured. The provider short-circuits to "no data" when false
    // rather than firing keyless requests that would just 401.
    public bool HasFinnhubApiKey => FinnhubApiKey.Length > 0;

    // Auto-refresh cadence in minutes; 0 means off. Bad/negative input falls back to the default.
    public int RefreshMinutes =>
        int.TryParse(_refreshMinutes.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v
            : DefaultRefreshMinutes;

    // Whether the poll loop should run at all (false when the user picked 0 = off).
    public bool AutoRefreshEnabled => RefreshMinutes > 0;

    // The cadence as a TimeSpan for the (upcoming) PeriodicTimer-driven poll loop.
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(RefreshMinutes);

    private MarketSettingsManager()
    {
        FilePath = Path.Combine(Utilities.BaseSettingsPath("Microsoft.CmdPal"), "market.settings.json");
        Settings.Add(_finnhubApiKey);
        Settings.Add(_refreshMinutes);
        LoadSettings();
        Settings.SettingsChanged += (_, _) => SaveSettings();
    }
}

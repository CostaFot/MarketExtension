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
// Settings: one API key per data provider, plus the price refresh interval (minutes; 0 = off). Keys
// are user-supplied at runtime — there is no built-in/baked key, and an empty key just means that
// provider is skipped. Keys are named per-provider because each provider has its own.
//
// Note: keys are NOT masked — the toolkit's TextSetting has no password mode; settings live in a
// local plaintext JSON file anyway.
//
// ⚠️ Toolkit quirk: TextSetting renders **Description** as the field's on-screen label (it maps Label →
// the Adaptive Card `title`, which Input.Text ignores). So the user-visible text must go in Description;
// Label is kept only as the semantic name.
internal sealed class MarketSettingsManager : JsonSettingsManager
{
    public static readonly MarketSettingsManager Instance = new();

    // Default auto-refresh cadence when the field is blank/unparseable.
    private const int DefaultRefreshMinutes = 5;

    private readonly TextSetting _twelveDataApiKey = new("twelveDataApiKey", string.Empty)
    {
        Label = "Twelve Data API key",
        Description = "Twelve Data API key. Used first when set; other providers serve only as fallback.",
        Placeholder = "Paste your API key",
    };

    private readonly TextSetting _finnhubApiKey = new("finnhubApiKey", string.Empty)
    {
        Label = "Finnhub API key",
        Description = "Finnhub API key.",
        Placeholder = "Paste your API key",
    };

    private readonly TextSetting _refreshMinutes = new(
        "refreshMinutes", DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture))
    {
        Label = "Price refresh interval (minutes)",
        Description = "Price refresh interval in minutes. Enter 0 to turn it off.",
        Placeholder = DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture),
    };

    // The key the Twelve Data provider should use — the user's setting, or empty when unset. Read as a
    // property (not cached) so a key change applies on the next request without a reload.
    public string TwelveDataApiKey => _twelveDataApiKey.Value?.Trim() ?? string.Empty;

    // Whether a Twelve Data key has been configured. The provider's Supports() returns false when this
    // is false, so the repository's first-match routing falls through to Finnhub/Frankfurter.
    public bool HasTwelveDataApiKey => TwelveDataApiKey.Length > 0;

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
        Settings.Add(_twelveDataApiKey);
        Settings.Add(_finnhubApiKey);
        Settings.Add(_refreshMinutes);
        LoadSettings();
        Settings.SettingsChanged += (_, _) => SaveSettings();
    }
}

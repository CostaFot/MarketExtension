using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// The status nudge for the list screens — one row, appended at the bottom, that explains the current data
// state. Two cases, in priority order:
//   * Demo mode ON  → a blue "Demo mode — showing sample data" row, so it's obvious prices are simulated
//     (not live). Takes priority because in demo mode there's data regardless of keys.
//   * No API key set → a red "No API key set" row: with NEITHER a Twelve Data nor a Finnhub key, stocks and
//     crypto can't be priced or searched (only keyless Frankfurter FX still works), so the app looks
//     broken/empty with no explanation.
// Otherwise null (a key is set and we're live → no nudge). The hub, Search, Watchlist and Favorites append
// StatusRow() when non-null; Enter on either row navigates straight into the extension's Settings page (to
// add a key, or to turn Demo mode off).
internal static class ApiKeyHint
{
    private const string WarningGlyph = ""; // Segoe MDL2 Warning
    private const string DemoGlyph = "";    // Segoe MDL2 Info

    // Windows "attention" red — used for the missing-key tag so the row reads as a problem, not a normal item.
    // A ListItem has no per-row text-color lever; a colored Tag (pill) is the only way to tint a row.
    private static OptionalColor WarningRed => ColorHelpers.FromRgb(0xD1, 0x34, 0x38);

    // Windows accent blue — informational, distinct from the red "problem" tag: demo mode is a deliberate
    // state, not a misconfiguration.
    private static OptionalColor DemoBlue => ColorHelpers.FromRgb(0x00, 0x78, 0xD4);

    // Built once and reused: the toolkit's navigable settings form over our settings singleton. Both status
    // rows point their Enter command at this, so the user lands directly on the relevant setting.
    private static IContentPage? _settingsPage;

    private static IContentPage SettingsPage =>
        _settingsPage ??= MarketSettingsManager.Instance.Settings.SettingsPage;

    // True when at least one pricing-provider key is set. Frankfurter (FX) is keyless and needs no key,
    // but stocks/crypto require a Twelve Data or Finnhub key — so "no key at all" means most of the app
    // shows no data. Reads the observable flag's current value; the surfaces also Subscribe to it so the
    // hint appears/disappears reactively the instant a key is added/cleared (MarketSettingsManager.HasAnyApiKey).
    private static bool HasAnyKey => MarketSettingsManager.Instance.HasAnyApiKey.Value;

    // The status row, or null when a key is set and demo mode is off (live → no nudge). Callers append it
    // only when non-null:
    //
    //     if (ApiKeyHint.StatusRow() is { } hint) items.Add(hint);
    public static IListItem? StatusRow()
    {
        if (MarketSettingsManager.Instance.DemoMode)
            return DemoRow();
        return HasAnyKey ? null : MissingKeyRow();
    }

    // "Demo mode — showing sample data": surfaced while the Demo-mode setting is on so the user knows the
    // prices are simulated. Enter → Settings (to turn it off).
    private static ListItem DemoRow() =>
        new ListItem(SettingsPage)
        {
            Title = "Demo mode — showing sample data",
            Subtitle = "Prices are simulated, not live. Turn off Demo mode in Settings (press Enter).",
            Icon = new IconInfo(DemoGlyph),
            Tags = [new Tag("Demo mode")
            {
                Foreground = DemoBlue,
                ToolTip = "Demo mode is on — showing built-in sample data, not live prices",
            }],
        };

    // "No API key set": surfaced when neither pricing key is configured (and demo mode is off). Enter →
    // Settings (to add a key).
    private static ListItem MissingKeyRow() =>
        new ListItem(SettingsPage)
        {
            Title = "No API key set — functionality is limited",
            Subtitle = "Add an API key in Settings (press Enter)",
            Icon = new IconInfo(WarningGlyph),
            Tags = [new Tag("Action required")
            {
                Foreground = WarningRed,
                ToolTip = "No API key set — functionality is limited",
            }],
        };
}

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// A "you haven't set an API key" nudge for the list screens. With NEITHER a Twelve Data nor a Finnhub
// key set, stocks and crypto can't be priced or searched (only keyless Frankfurter FX still works), so
// the app looks broken/empty with no explanation. Rather than leave the user guessing, the hub, Search,
// Watchlist and Favorites append this row at the bottom when no key is configured; Enter navigates
// straight into the extension's Settings page (the same form the Markets hub links to).
internal static class ApiKeyHint
{
    private const string WarningGlyph = "\uE7BA"; // Segoe MDL2 Warning

    // Windows "attention" red \u2014 used for the warning tag so the row reads as a problem, not a normal item.
    // A ListItem has no per-row text-color lever; a colored Tag (pill) is the only way to tint a row.
    private static OptionalColor WarningRed => ColorHelpers.FromRgb(0xD1, 0x34, 0x38);

    // Built once and reused: the toolkit's navigable settings form over our settings singleton. The
    // missing-key row points its Enter command at this, so the user lands directly on the key fields.
    private static IContentPage? _settingsPage;

    private static IContentPage SettingsPage =>
        _settingsPage ??= MarketSettingsManager.Instance.Settings.SettingsPage;

    // True when at least one pricing-provider key is set. Frankfurter (FX) is keyless and needs no key,
    // but stocks/crypto require a Twelve Data or Finnhub key — so "no key at all" means most of the app
    // shows no data. Reads the observable flag's current value; the surfaces also Subscribe to it so the
    // hint appears/disappears reactively the instant a key is added/cleared (MarketSettingsManager.HasAnyApiKey).
    private static bool HasAnyKey => MarketSettingsManager.Instance.HasAnyApiKey.Value;

    // The hint row, or null when a key is already configured. Callers append it only when non-null:
    //
    //     if (ApiKeyHint.MissingKeyRow() is { } hint) items.Add(hint);
    public static IListItem? MissingKeyRow() =>
        HasAnyKey
            ? null
            : new ListItem(SettingsPage)
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

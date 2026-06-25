using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using MarketExtension.Properties;

namespace MarketExtension;

// An advisory "we're being rate-limited" banner for the priced screens, shown while RateLimitSignal is set
// (a request stayed 429 after backing off — see HttpRetry) AND the user hasn't hidden it via the "Show
// rate-limit warnings" setting (MarketSettingsManager.ShowRateLimitErrors). It tells the user the prices on
// screen are the last known ones (the keep-last-good guard holds them) and will refresh once the window
// clears, instead of leaving them to guess why values look stale. Enter does NOTHING (a NoOpCommand): it's
// the default-selected first row, so it must not steal Enter or navigate away — it's purely informational.
// Parallels ApiKeyHint.StatusRow().
internal static class RateLimitHint
{
    private const string WarningGlyph = ""; // Segoe MDL2 Warning

    // Amber (caution) — distinct from ApiKeyHint's red (an actual misconfiguration); rate-limiting is
    // transient, not broken. A colored Tag is the only per-row tint a ListItem allows.
    private static OptionalColor CautionAmber => ColorHelpers.FromRgb(0xC8, 0x7C, 0x00);

    // The banner row, or null when we're not throttled (or the user hid it via the "Show rate-limit
    // warnings" setting, or demo mode is on). Callers append it only when non-null:
    //
    //     if (RateLimitHint.Row() is { } banner) items.Add(banner);
    //
    // Demo mode makes no live requests, so throttling is meaningless there — and a signal set before demo
    // was turned on would otherwise never clear (nothing calls ReportSuccess() offline). Guard on it here so
    // the banner can never show while demoing, regardless of a stale signal. RateLimitSignal ALSO clears the
    // flag when demo mode is enabled (so turning it back off doesn't flash the old banner).
    public static IListItem? Row() =>
        !MarketSettingsManager.Instance.DemoMode &&
        RateLimitSignal.Instance.IsRateLimited.Value && MarketSettingsManager.Instance.ShowRateLimitErrors
            ? new ListItem(new NoOpCommand()) // informational only — Enter must not navigate (it's the first row)
            {
                Title = Resources.RateLimit_Title,
                Subtitle = Resources.RateLimit_Subtitle,
                Icon = new IconInfo(WarningGlyph),
                Tags = [new Tag(Resources.RateLimit_Tag)
                {
                    Foreground = CautionAmber,
                    ToolTip = Resources.RateLimit_Tooltip,
                }],
            }
            : null;
}

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using MarketExtension.Properties;

namespace MarketExtension;

// The active live data provider's attribution, shown wherever its data is displayed. Twelve Data's terms
// REQUIRE attribution ("Data provided by Twelve Data", with a link) on any surface that shows their data;
// Finnhub gets the same treatment as good practice / store-reviewer comfort (its terms don't mandate it).
// Reflects the ACTIVE source so it's never wrong — and never credits a provider whose data isn't on screen:
//   * Demo mode            -> null (no live data is shown; the demo status row already says "sample data")
//   * Twelve Data key set  -> Twelve Data (TD serves every category, so its must-do is always covered)
//   * else Finnhub key set -> Finnhub
//   * else                 -> null (nothing is being priced; the missing-key hint explains the blanks)
//
// The row is clickable and opens the provider's site — the desktop-app equivalent of Twelve Data's required
// "dofollow link to twelvedata.com" (a CmdPal extension has no <a rel>, so a launch command is the faithful
// analog). Parallels ApiKeyHint / RateLimitHint: a static helper returning a row (or text) or null. The
// keyless FX source (Frankfurter / ECB) and the logo source (Elbstream) are credited separately — ECB on
// the Data Sources page, Elbstream via AssetIconResolver.AttributionRow().
internal static class DataSourceAttribution
{
    private const string LinkGlyph = ""; // Segoe MDL2 Link

    // The attribution row for a list page, or null when no live provider is serving (demo / no key).
    public static IListItem? Row()
    {
        var (name, url) = Active();
        if (name is null)
            return null;

        return new ListItem(new OpenUrlCommand(url!, Strings.Format(Resources.DataAttribution_OpenName, name)))
        {
            Title = Strings.Format(Resources.DataAttribution_Title, name),
            Subtitle = Resources.DataAttribution_Subtitle,
            Icon = new IconInfo(LinkGlyph),
        };
    }

    // The same credit as plain text (for the symbol-detail adaptive card), or null when none applies.
    public static string? Text()
    {
        var (name, _) = Active();
        return name is null ? null : Strings.Format(Resources.DataAttribution_Title, name);
    }

    // (display name, site URL) of the active live data source, or (null, null) when none is serving.
    // Brand names are intentionally NOT localized.
    private static (string? Name, string? Url) Active()
    {
        var settings = MarketSettingsManager.Instance;
        if (settings.DemoMode)
            return (null, null);
        if (settings.HasTwelveDataApiKey)
            return ("Twelve Data", "https://twelvedata.com");
        if (settings.HasFinnhubApiKey)
            return ("Finnhub", "https://finnhub.io");
        return (null, null);
    }
}

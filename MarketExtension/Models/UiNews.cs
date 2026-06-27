using System;
using MarketExtension.Properties;

namespace MarketExtension;

// UI layer: the presentation projection of a DomainNews. This is the ONLY place news formatting lives
// (relative "2h ago" times, the source · time subtitle), so the Domain and Api layers stay free of UI
// concerns. Deliberately holds NO toolkit types (IconInfo/Details) — the page builds those from these
// strings, exactly as the priced surfaces build IconInfo from UiQuote. Pages render UiNews and filter on the
// pass-through members.
internal sealed record UiNews(DomainNews Source)
{
    public static UiNews From(DomainNews n) => new(n);

    public string Headline => Source.Headline;
    public string SourceName => Source.Source;
    public string Summary => Source.Summary;
    public string ArticleUrl => Source.ArticleUrl;
    public string? ImageUrl => Source.ImageUrl;

    // The row subtitle: "CNBC · 2h ago" (the source is dropped if unknown). "·" is punctuation, not
    // localizable text, so it stays inline like UiQuote's ▲/▼ glyphs.
    public string FormatSubtitle() => string.IsNullOrWhiteSpace(SourceName)
        ? FormatRelativeTime()
        : $"{SourceName} · {FormatRelativeTime()}";

    // Coarse relative age — "just now" / "5m ago" / "2h ago" / "3d ago". Compared against UTC now; a future
    // timestamp (clock skew) clamps to "just now" rather than rendering a negative age.
    public string FormatRelativeTime()
    {
        var delta = DateTimeOffset.UtcNow - Source.Published;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalMinutes < 1)
            return Resources.News_Time_JustNow;
        if (delta.TotalHours < 1)
            return Strings.Format(Resources.News_Time_MinutesAgo, (int)delta.TotalMinutes);
        if (delta.TotalDays < 1)
            return Strings.Format(Resources.News_Time_HoursAgo, (int)delta.TotalHours);
        return Strings.Format(Resources.News_Time_DaysAgo, (int)delta.TotalDays);
    }

    // Client-side filter for the page's typed search box: match the query against headline / source / summary.
    public bool Matches(string query) =>
        Headline.Contains(query, StringComparison.OrdinalIgnoreCase)
        || SourceName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Summary.Contains(query, StringComparison.OrdinalIgnoreCase);
}

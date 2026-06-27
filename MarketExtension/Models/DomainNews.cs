using System;

namespace MarketExtension;

// Domain layer: a provider-agnostic, presentation-free market-news article. This is what a news
// provider produces (mapped from its Api* DTO, e.g. ApiFinnhubNewsDto). No formatting lives here — a
// Ui* projection would own "2h ago" relative times, truncated summaries, etc.
//
// Like DomainInstrument (and unlike DomainQuote), there is no IsValid flag: a news feed is a list, so a
// provider simply drops items missing the essentials (id / headline / url) at its mapping boundary and
// returns the survivors. Id is Finnhub's news id, reusable as the `minId` cursor to fetch only newer
// items. Published is the article time as a proper instant (the Api datetime is UNIX seconds). ImageUrl
// (thumbnail) and Related (comma-separated tickers) are optional — many articles omit them.
internal sealed record DomainNews(
    long Id,
    string Headline,
    string Source,
    string Summary,
    string Category,
    string ArticleUrl,
    DateTimeOffset Published,
    string? ImageUrl = null,
    string? Related = null);

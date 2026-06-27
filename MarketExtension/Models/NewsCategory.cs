namespace MarketExtension;

// Provider-agnostic shared vocabulary (like AssetCategory / ChartRange): the topic of a market-news
// feed. It carries only neutral intent; the translation to a specific data source's category token
// lives in that provider (e.g. FinnhubMarketDataProvider.ToFinnhubNewsCategory), so a new news source
// plugs in without touching this.
internal enum NewsCategory
{
    General,
    Forex,
    Crypto,
    Merger,
}

internal static class NewsCategoryExtensions
{
    // The categories, in tab order.
    public static readonly NewsCategory[] All =
    [
        NewsCategory.General,
        NewsCategory.Forex,
        NewsCategory.Crypto,
        NewsCategory.Merger,
    ];

    // Short, human-readable tab label.
    public static string Label(this NewsCategory category) => category switch
    {
        NewsCategory.General => "General",
        NewsCategory.Forex => "Forex",
        NewsCategory.Crypto => "Crypto",
        NewsCategory.Merger => "Mergers",
        _ => "General",
    };
}

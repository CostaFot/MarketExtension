using System;

namespace MarketExtension;

// Provider-agnostic shared vocabulary (like AssetCategory): the time window a price chart covers.
// It carries only neutral intent — a label, a lookback window, and a neutral CandleInterval. The
// translation to a specific data source's resolution token lives in that provider (e.g.
// FinnhubMarketDataProvider.ToFinnhubResolution), so a new provider plugs in without touching this.
internal enum ChartRange
{
    OneDay,
    OneWeek,
    OneMonth,
    OneYear,
    FiveYear,
}

// Provider-agnostic bar size. Each provider maps this to its own API token (Finnhub: 5/30/60/D/W;
// a Twelve Data provider would map the same values to 5min/30min/1h/1day/1week).
internal enum CandleInterval
{
    FiveMin,
    ThirtyMin,
    Hourly,
    Daily,
    Weekly,
}

internal static class ChartRangeExtensions
{
    // The five ranges, in tab order.
    public static readonly ChartRange[] All =
    [
        ChartRange.OneDay,
        ChartRange.OneWeek,
        ChartRange.OneMonth,
        ChartRange.OneYear,
        ChartRange.FiveYear,
    ];

    // Short tab label, e.g. "1D". Also the Action.Submit payload value (see FromLabel).
    public static string Label(this ChartRange range) => range switch
    {
        ChartRange.OneDay => "1D",
        ChartRange.OneWeek => "1W",
        ChartRange.OneMonth => "1M",
        ChartRange.OneYear => "1Y",
        ChartRange.FiveYear => "5Y",
        _ => "1D",
    };

    // How far back the chart looks. A provider computes its request window as from = now - Lookback.
    public static TimeSpan Lookback(this ChartRange range) => range switch
    {
        ChartRange.OneDay => TimeSpan.FromDays(1),
        ChartRange.OneWeek => TimeSpan.FromDays(7),
        ChartRange.OneMonth => TimeSpan.FromDays(31),
        ChartRange.OneYear => TimeSpan.FromDays(365),
        ChartRange.FiveYear => TimeSpan.FromDays(365 * 5),
        _ => TimeSpan.FromDays(1),
    };

    // The neutral bar size that reads well for this window — intraday for short ranges, coarser for
    // long ones. Providers translate this to their own resolution token.
    public static CandleInterval Interval(this ChartRange range) => range switch
    {
        ChartRange.OneDay => CandleInterval.FiveMin,
        ChartRange.OneWeek => CandleInterval.ThirtyMin,
        ChartRange.OneMonth => CandleInterval.Hourly,
        ChartRange.OneYear => CandleInterval.Daily,
        ChartRange.FiveYear => CandleInterval.Weekly,
        _ => CandleInterval.Daily,
    };

    // Parse a tab label (the Action.Submit payload) back to a range; unknown -> 1D.
    public static ChartRange FromLabel(string? label) => label switch
    {
        "1D" => ChartRange.OneDay,
        "1W" => ChartRange.OneWeek,
        "1M" => ChartRange.OneMonth,
        "1Y" => ChartRange.OneYear,
        "5Y" => ChartRange.FiveYear,
        _ => ChartRange.OneDay,
    };
}

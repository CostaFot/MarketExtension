using System.Globalization;
using System.Linq;

namespace MarketExtension;

// UI layer: the presentation projection of a DomainCandleSeries — the ONLY place chart formatting
// and SVG generation live (the chart-series analog of UiQuote). Provider-independent: it renders the
// same regardless of which IMarketDataProvider produced the data.
internal sealed record UiCandleSeries(DomainCandleSeries Source)
{
    public static UiCandleSeries From(DomainCandleSeries series) => new(series);

    public string Symbol => Source.Symbol;
    public ChartRange Range => Source.Range;
    public bool HasData => Source.HasData;

    // Net direction over the selected window (last vs. first close). Up when flat or rising — drives
    // the green/red color of both the change text and the chart line.
    public bool IsUp => Source.LastClose >= Source.FirstClose;

    // Latest close in the window, formatted as money. A dash when there's no data.
    public string FormatPrice() => !HasData
        ? "—"
        : Source.LastClose.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    // Net change across the SELECTED range (like Robinhood — the % matches the chosen tab, not the
    // trading day), e.g. "▲ +$3.20 (+1.71%) · 1M". Empty when there's no data.
    public string FormatRangeChange()
    {
        if (!HasData)
            return string.Empty;

        var first = Source.FirstClose;
        var change = Source.LastClose - first;
        var pct = first == 0m ? 0m : change / first * 100m;
        var arrow = IsUp ? "▲" : "▼";
        var amount = change.ToString("+$#,##0.00;-$#,##0.00", CultureInfo.InvariantCulture);
        var percent = pct.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        return $"{arrow} {amount} ({percent}%) · {Range.Label()}";
    }

    // The chart as an inline SVG data URI (empty when there's no data), recolored by direction.
    public string ChartImageUrl() => !HasData
        ? string.Empty
        : ChartHelper.CreateImageUrl([.. Source.Points.Select(p => p.Close)], IsUp);
}

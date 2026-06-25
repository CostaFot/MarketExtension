using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketExtension;

// UI layer: the rolled-up totals across a set of UiPositions — total market value and today's P&L for the
// whole portfolio, expressed in the user's reporting (preferred) currency. Rendered as the summary row
// pinned to the top of the Portfolio screen. Like the other Ui* types, the only place its formatting lives.
//
// A holding counts toward the totals only when it's both VALID (priced) AND convertible into the preferred
// currency (UiPosition.CountsTowardTotal) — its CONVERTED value/P&L is summed. Two kinds of holding are
// therefore left out: an unpriced one (unknown symbol / fetch failure) and one whose native currency the FX
// provider can't convert. UnconvertedCount surfaces the latter so the user knows the total is partial; an
// all-USD portfolio with USD preferred converts trivially (rate 1) and excludes nothing.
internal sealed record UiPortfolio(decimal TotalValue, decimal TotalDailyPnL, string Currency, bool HasHoldings, int UnconvertedCount)
{
    public static UiPortfolio From(IEnumerable<UiPosition> positions, string preferredCurrency)
    {
        var all = positions.ToList();
        var counted = all.Where(p => p.CountsTowardTotal).ToList();
        var totalValue = counted.Sum(p => p.ConvertedMarketValue ?? 0m);
        var totalPnL = counted.Sum(p => p.ConvertedDailyPnL ?? 0m);
        // Priced but not convertible — present in the list, missing from the total.
        var unconverted = all.Count(p => p.IsValid && !p.IsConverted);
        return new UiPortfolio(totalValue, totalPnL, preferredCurrency, all.Count > 0, unconverted);
    }

    public bool IsUp => TotalDailyPnL >= 0;

    // Aggregate daily percent measured against YESTERDAY'S close value (today's value minus today's
    // gain), not an average of the per-position percents. Guards a zero denominator.
    public decimal TotalDailyPnLPercent
    {
        get
        {
            var previousClose = TotalValue - TotalDailyPnL;
            return previousClose == 0m ? 0m : TotalDailyPnL / previousClose * 100m;
        }
    }

    public string FormatTotalValue() => CurrencyFormat.Format(TotalValue, Currency);

    // e.g. "▲ +$120.50 (+0.98%) today" / "▼ -$84.10 (-0.71%) today", in the preferred currency.
    public string FormatTotalChange() =>
        $"{(IsUp ? "▲" : "▼")} {CurrencyFormat.FormatSigned(TotalDailyPnL, Currency)} " +
        $"({TotalDailyPnLPercent.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}%) today";

    // A trailing note for the summary subtitle when some priced holdings couldn't be converted into the
    // preferred currency (so the total visibly excludes them); empty when everything converted.
    public string FormatUnconvertedNote() => UnconvertedCount switch
    {
        0 => string.Empty,
        1 => " · 1 holding not converted",
        _ => $" · {UnconvertedCount} holdings not converted",
    };
}

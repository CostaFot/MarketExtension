using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketExtension;

// UI layer: the rolled-up totals across a set of UiPositions — total market value and today's P&L for
// the whole portfolio. Rendered as the summary row pinned to the top of the Portfolio screen. Like the
// other Ui* types, the only place its formatting lives.
//
// Only VALID (priced) positions count toward the totals; an unpriced holding (unknown symbol, fetch
// failure) is shown as its own unpriced row but left out of the rollup, so a transient bad quote can't
// distort the total. Single-currency assumption (USD) holds for this pass — values are summed as-is and
// shown with "$"; FX conversion via the PortfolioCurrency setting is future work.
internal sealed record UiPortfolio(decimal TotalValue, decimal TotalDailyPnL, bool HasHoldings)
{
    public static UiPortfolio From(IEnumerable<UiPosition> positions)
    {
        var all = positions.ToList();
        var priced = all.Where(p => p.IsValid).ToList();
        var totalValue = priced.Sum(p => p.MarketValue);
        var totalPnL = priced.Sum(p => p.DailyPnL);
        return new UiPortfolio(totalValue, totalPnL, all.Count > 0);
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

    public string FormatTotalValue() => TotalValue.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    // e.g. "▲ +$120.50 (+0.98%) today" / "▼ -$84.10 (-0.71%) today".
    public string FormatTotalChange() =>
        $"{(IsUp ? "▲" : "▼")} " +
        $"{TotalDailyPnL.ToString("+$#,##0.00;-$#,##0.00", CultureInfo.InvariantCulture)} " +
        $"({TotalDailyPnLPercent.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}%) today";
}

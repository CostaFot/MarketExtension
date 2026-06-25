using System.Globalization;

namespace MarketExtension;

// UI layer: the presentation projection of a priced holding — a DomainQuote combined with the quantity
// held. Like UiQuote, this is the ONLY place its formatting lives, so the Domain layer stays free of
// presentation concerns. The Portfolio screen renders these rows; UiPortfolio rolls a set of them up
// into totals.
//
// This pass shows DAILY P&L only: market value (qty × price) and today's gain/loss in $ and %. The
// daily-change inputs (DomainQuote.Change / ChangePercent) are exactly what MarketRepository already
// returns, so no new data is fetched. Cost-basis / total-return reporting is deferred (see DomainPosition).
internal sealed record UiPosition(DomainQuote Source, decimal Quantity)
{
    public static UiPosition From(DomainQuote quote, decimal quantity) => new(quote, quantity);

    public string Symbol => Source.Symbol;
    public string Name => Source.Name;
    public AssetCategory Category => Source.Category;
    public bool IsValid => Source.IsValid;
    public bool IsUp => Source.Change >= 0;

    // What the holding is worth right now (qty × live price) and today's gain/loss on it (qty × the
    // per-unit daily change). Both are 0 on an invalid quote and excluded from the rolled-up totals.
    public decimal MarketValue => IsValid ? Quantity * Source.Price : 0m;
    public decimal DailyPnL => IsValid ? Quantity * Source.Change : 0m;

    // The quantity without trailing zeros (10, not 10.00; 0.5 stays 0.5).
    public string FormatQuantity() => Quantity.ToString("0.########", CultureInfo.InvariantCulture);

    // "AAPL · 10 sh" / "BTC · 0.5 units". Forex is shown in "units" too (a notional holding of a pair).
    public string FormatHolding() => $"{Symbol} · {FormatQuantity()} {UnitLabel}";

    private string UnitLabel => Category == AssetCategory.Stock ? "sh" : "units";

    public string FormatMarketValue() => !IsValid
        ? "—"
        : MarketValue.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    // e.g. "▲ +$12.05 (+1.20%)" / "▼ -$8.40 (-0.80%)"; empty for an invalid quote.
    public string FormatDailyPnL() => !IsValid
        ? string.Empty
        : $"{(IsUp ? "▲" : "▼")} " +
          $"{DailyPnL.ToString("+$#,##0.00;-$#,##0.00", CultureInfo.InvariantCulture)} " +
          $"({Source.ChangePercent.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}%)";
}

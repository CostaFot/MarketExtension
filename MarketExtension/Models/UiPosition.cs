namespace MarketExtension;

// UI layer: the presentation projection of a priced holding — a DomainQuote combined with the quantity
// held, valued in BOTH the instrument's native currency and the user's reporting (preferred) currency.
// Like UiQuote, this is the ONLY place its formatting lives, so the Domain layer stays free of presentation
// concerns. The Portfolio screen renders these rows; UiPortfolio rolls a set of them up into totals.
//
// Two flavours of gain/loss are shown:
//   * DAILY P&L — qty × the per-unit daily change (vs. yesterday's close); always available on a valid quote.
//   * TOTAL RETURN — qty × (price − cost basis), the unrealized gain since purchase; shown only when the
//     holding has a recorded CostBasis (per-unit price paid, in the instrument's NATIVE currency). A holding
//     with no basis simply omits it.
// Multi-currency:
//   * The native value/P&L/cost come straight from the quote (qty × price/change/basis in DomainQuote.Currency).
//   * RateToPreferred is units of PreferredCurrency per 1 unit of the native currency (1 when they match,
//     null when the FX rate isn't available — see CurrencyConverter). When known, the native amounts convert
//     and the row shows native AND converted, and the holding counts toward the portfolio total. When null,
//     the row shows native only and is excluded from the total (CountsTowardTotal == false).
internal sealed record UiPosition(DomainQuote Source, decimal Quantity, string PreferredCurrency, decimal? RateToPreferred, decimal? CostBasis = null)
{
    public static UiPosition From(DomainQuote quote, decimal quantity, string preferredCurrency, decimal? rateToPreferred, decimal? costBasis = null)
        => new(quote, quantity, preferredCurrency, rateToPreferred, costBasis);

    public string Symbol => Source.Symbol;
    public string Name => Source.Name;
    public AssetCategory Category => Source.Category;
    public bool IsValid => Source.IsValid;
    public bool IsUp => Source.Change >= 0;

    public string NativeCurrency => Source.Currency;
    public bool NeedsConversion =>
        !string.Equals(NativeCurrency, PreferredCurrency, System.StringComparison.OrdinalIgnoreCase);
    public bool IsConverted => RateToPreferred.HasValue;

    // What the holding is worth right now (qty × live price) and today's gain/loss on it (qty × the
    // per-unit daily change), in the NATIVE currency. Both are 0 on an invalid quote.
    public decimal MarketValue => IsValid ? Quantity * Source.Price : 0m;
    public decimal DailyPnL => IsValid ? Quantity * Source.Change : 0m;

    // The same values in the PREFERRED currency, or null when the rate is unavailable (or the quote invalid).
    public decimal? ConvertedMarketValue => IsValid && RateToPreferred is { } r ? MarketValue * r : null;
    public decimal? ConvertedDailyPnL => IsValid && RateToPreferred is { } r ? DailyPnL * r : null;

    // A holding contributes to the portfolio total only when it's priced AND convertible into the
    // preferred currency (a USD holding with USD preferred trivially has rate 1).
    public bool CountsTowardTotal => IsValid && RateToPreferred.HasValue;

    // Total-return (unrealized P&L since purchase). Only meaningful when a positive cost basis was recorded;
    // a basis of 0/null means "unknown" and these stay null so the UI omits the figure.
    public bool HasCostBasis => CostBasis is > 0m;

    // What was paid for the whole holding (qty × per-unit basis), native and converted.
    public decimal? TotalCost => HasCostBasis ? Quantity * CostBasis : null;
    public decimal? ConvertedTotalCost => TotalCost is { } c && RateToPreferred is { } r ? c * r : null;

    // Current value minus what was paid = the unrealized gain/loss since purchase, native and converted.
    public decimal? TotalReturn => IsValid && TotalCost is { } c ? MarketValue - c : null;
    public decimal? ConvertedTotalReturn => TotalReturn is { } tr && RateToPreferred is { } r ? tr * r : null;

    // Return as a percentage of cost: (price − basis) / basis. Currency-independent; null without a basis or
    // a live price. Computed from the per-unit figures so it's exact regardless of quantity.
    public decimal? TotalReturnPercent => IsValid && CostBasis is { } cb && cb > 0m
        ? (Source.Price - cb) / cb * 100m
        : null;

    public bool IsTotalReturnUp => (TotalReturn ?? 0m) >= 0m;

    // The quantity without trailing zeros (10, not 10.00; 0.5 stays 0.5).
    public string FormatQuantity() => Quantity.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    // "AAPL · 10 sh" / "BTC · 0.5 units". Forex is shown in "units" too (a notional holding of a pair).
    public string FormatHolding() => $"{Symbol} · {FormatQuantity()} {UnitLabel}";

    private string UnitLabel => Category == AssetCategory.Stock ? "sh" : "units";

    // Native value, with a converted approximation appended when the currencies differ and a rate is known:
    //   USD holding, USD preferred → "$1,234.56"
    //   GBP holding, USD preferred → "£75.00 (≈$95.20)"
    //   GBP holding, USD preferred, no rate → "£75.00" (excluded from the total)
    public string FormatValue()
    {
        if (!IsValid)
            return "—";

        var native = CurrencyFormat.Format(MarketValue, NativeCurrency);
        if (!NeedsConversion)
            return native;
        return ConvertedMarketValue is { } converted
            ? $"{native} (≈{CurrencyFormat.Format(converted, PreferredCurrency)})"
            : native;
    }

    // e.g. "▲ +$12.05 (+1.20%)" / "▼ -$8.40 (-0.80%)"; empty for an invalid quote. Shown in the converted
    // (preferred) currency when convertible — so it matches the portfolio total — else in the native one.
    // The percent is currency-independent.
    public string FormatDailyPnL()
    {
        if (!IsValid)
            return string.Empty;

        var (amount, currency) = NeedsConversion && ConvertedDailyPnL is { } c
            ? (c, PreferredCurrency)
            : (DailyPnL, NativeCurrency);

        return $"{(IsUp ? "▲" : "▼")} {CurrencyFormat.FormatSigned(amount, currency)} " +
               $"({Source.ChangePercent.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture)}%)";
    }

    // e.g. "▲ +$420.00 (+12.00%)" — the unrealized gain/loss since purchase. Empty when no cost basis is
    // recorded or the quote is invalid. Like FormatDailyPnL it shows the converted (preferred-currency)
    // amount when convertible — so it matches the portfolio total — else the native one; the percent is
    // currency-independent. The caller prefixes its own label ("Total") to distinguish it from daily P&L.
    public string FormatTotalReturn()
    {
        if (!IsValid || TotalReturn is not { } native)
            return string.Empty;

        var (amount, currency) = NeedsConversion && ConvertedTotalReturn is { } c
            ? (c, PreferredCurrency)
            : (native, NativeCurrency);

        var percent = TotalReturnPercent is { } p
            ? $" ({p.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture)}%)"
            : string.Empty;

        return $"{(IsTotalReturnUp ? "▲" : "▼")} {CurrencyFormat.FormatSigned(amount, currency)}{percent}";
    }
}

using System.Globalization;

namespace MarketExtension;

// UI layer: the presentation projection of a DomainQuote. This is the ONLY place price/change
// formatting lives, so the Domain and Api layers stay free of UI concerns. Pages render UiQuote
// and filter/group on the pass-through identity members (Symbol / Name / Category).
internal sealed record UiQuote(DomainQuote Source)
{
    public static UiQuote From(DomainQuote quote) => new(quote);

    public string Symbol => Source.Symbol;
    public string Name => Source.Name;
    public AssetCategory Category => Source.Category;
    public bool IsValid => Source.IsValid;
    public bool IsUp => Source.Change >= 0;

    // Currency pairs are quoted to 4 decimals (e.g. 1.0832 — an exchange rate, not a money amount, so no
    // symbol); stocks/crypto as money in their NATIVE currency (a London stock shows "£2.50", not "$2.50").
    // Invalid quotes (unknown symbol, unavailable, or a fetch failure) render as a dash.
    public string FormatPrice() => !IsValid
        ? "—"
        : Category == AssetCategory.Currency
            ? Source.Price.ToString("0.0000", CultureInfo.InvariantCulture)
            : CurrencyFormat.Format(Source.Price, Source.Currency);

    // e.g. "▲ +1.20%" / "▼ -0.80%"; empty for invalid quotes.
    public string FormatChange() => !IsValid
        ? string.Empty
        : $"{(IsUp ? "▲" : "▼")} {Source.ChangePercent.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}%";
}

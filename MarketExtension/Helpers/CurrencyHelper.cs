using System;

namespace MarketExtension;

// Small currency conventions shared by the providers when they stamp DomainQuote.Currency. Two jobs:
//
//   * NormalizeStockQuote — turn a raw provider currency code (which may be a MINOR unit) plus its
//     price/change into a clean ISO-4217 code with price/change in MAJOR units. The case that matters:
//     London-listed stocks are quoted in PENCE while the reported currency is "GBp"/"GBX", so a price of
//     250 means £2.50 — a 100× factor that would otherwise make UK holdings 100× too large. We fold that
//     here so the Domain layer only ever sees major-unit prices in a major-unit currency code.
//   * QuoteCurrencyOfPair — the currency an FX pair's price is denominated in: the QUOTE (second) currency
//     of a 6-letter BASE+QUOTE pair (EURUSD → USD, USDJPY → JPY), so a notional FX holding values correctly.
//
// Keeping the GBX rule in ONE place means every provider that maps a stock quote (Twelve Data, Finnhub)
// normalizes it identically.
internal static class CurrencyHelper
{
    // The minor-unit codes we know to be 1/100 of their major unit. "GBp" (pence, lowercase p) is the
    // common one; "GBX" is the same thing under the exchange's alias. Compared case-sensitively for "GBp"
    // (so it isn't confused with "GBP" pounds) and case-insensitively for "GBX".
    private static bool IsPence(string code) =>
        code == "GBp" || string.Equals(code, "GBX", StringComparison.OrdinalIgnoreCase);

    // Normalize a stock's raw (code, price, change) into a major-unit (code, price, change). A null/blank
    // code defaults to USD (the overwhelming common case and the app's prior assumption). Pence-quoted
    // codes divide price AND change by 100 and report GBP; everything else just upper-cases the code.
    // Percent change is unit-free, so it never needs adjusting.
    public static (string Currency, decimal Price, decimal Change) NormalizeStockQuote(
        string? rawCurrency, decimal price, decimal change)
    {
        var code = rawCurrency?.Trim();
        if (string.IsNullOrEmpty(code))
            return ("USD", price, change);

        if (IsPence(code))
            return ("GBP", price / 100m, change / 100m);

        return (code.ToUpperInvariant(), price, change);
    }

    // Resolve just the ISO code from a raw provider currency (e.g. for a price already in major units, or
    // when we only need the code). Mirrors NormalizeStockQuote's code rule.
    public static string NormalizeCode(string? rawCurrency)
    {
        var code = rawCurrency?.Trim();
        if (string.IsNullOrEmpty(code))
            return "USD";
        return IsPence(code) ? "GBP" : code.ToUpperInvariant();
    }

    // The currency an FX pair is priced in = its quote (second) currency. A neutral pair is 6 letters
    // (BASE+QUOTE); anything else falls back to USD rather than throwing.
    public static string QuoteCurrencyOfPair(string pairSymbol)
    {
        if (string.IsNullOrWhiteSpace(pairSymbol) || pairSymbol.Length != 6)
            return "USD";
        return pairSymbol[3..].ToUpperInvariant();
    }
}

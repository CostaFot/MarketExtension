using System.Collections.Generic;
using System.Globalization;

namespace MarketExtension;

// UI helper: render a decimal amount as money in a given ISO-4217 currency. This is where the per-currency
// SYMBOL ("$", "€", "£", "¥") and decimal-place rules live, so the Ui layer no longer hardcodes "$".
// Used by UiQuote (instrument price), UiPosition (holding value / P&L) and UiPortfolio (totals).
//
// Formatting is deliberately simple and culture-INVARIANT (the app formats numbers invariantly everywhere
// else): "<symbol><grouped amount>", e.g. "$1,234.56", "£2.50", "¥156,000". A code we don't have a glyph
// for falls back to a trailing code: "1,234.56 SGD". Most-zero-decimal currencies (JPY/KRW) render with no
// fractional part. Signed variants put the sign before the symbol ("+$12.05" / "-$8.40") for P&L.
internal static class CurrencyFormat
{
    // ISO-4217 code → display symbol. Covers the currencies offered as PortfolioCurrency plus the common
    // native quote currencies; anything missing falls back to the bare code.
    private static readonly Dictionary<string, string> Symbols = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = "$",  ["EUR"] = "€",  ["GBP"] = "£",  ["JPY"] = "¥",  ["CNY"] = "¥",
        ["AUD"] = "A$", ["CAD"] = "C$", ["NZD"] = "NZ$", ["HKD"] = "HK$", ["SGD"] = "S$",
        ["CHF"] = "CHF ", ["SEK"] = "kr ", ["NOK"] = "kr ", ["DKK"] = "kr ",
        ["PLN"] = "zł ", ["ZAR"] = "R ", ["MXN"] = "Mex$", ["INR"] = "₹", ["BRL"] = "R$",
        ["KRW"] = "₩",
    };

    // Currencies conventionally shown without a fractional part.
    private static int Decimals(string code) =>
        code is "JPY" or "KRW" ? 0 : 2;

    // "$1,234.56" / "¥156,000" / "1,234.56 SGD" — an unsigned money amount.
    public static string Format(decimal amount, string code)
    {
        var number = amount.ToString(NumberPattern(code), CultureInfo.InvariantCulture);
        return Symbols.TryGetValue(code, out var symbol)
            ? symbol + number
            : $"{number} {code.ToUpperInvariant()}";
    }

    // "+$12.05" / "-$8.40" / "+156 JPY" — a signed money amount (the sign precedes the symbol). Used for
    // P&L, where the leading +/- reads more naturally than a parenthesized negative.
    public static string FormatSigned(decimal amount, string code)
    {
        var sign = amount < 0m ? "-" : "+";
        return sign + Format(System.Math.Abs(amount), code);
    }

    private static string NumberPattern(string code) =>
        Decimals(code) == 0 ? "#,##0" : "#,##0.00";
}

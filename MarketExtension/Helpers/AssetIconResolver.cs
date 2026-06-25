using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using MarketExtension.Properties;

namespace MarketExtension;

// Resolves an instrument's identity to a row/dock IconInfo. This is the single place the logo-URL
// convention lives. Logos come from Elbstream's keyless CDN (https://elbstream.com), addressed
// directly by symbol — so there's no API call, no caching layer, and no DTO: new IconInfo(url) just
// hands the URL to the CmdPal host, which fetches the image itself.
//
// Stocks/crypto resolve to their company/coin logo; an FX pair resolves to its BASE currency's
// country flag (Elbstream /logos/country/{iso2} — EUR maps to the EU flag). Currencies we don't map
// and unknown categories fall back to a per-category Segoe MDL2 glyph. Elbstream is free WITH
// ATTRIBUTION — every surface that shows a logo must carry the credit (see AttributionRow + its
// placements in the priced/search pages and the detail card).
internal static class AssetIconResolver
{
    private const string Base = "https://api.elbstream.com/logos";

    // Per-category fallback glyphs (Segoe MDL2 Assets), used only where no remote logo is fetched.
    private const string CurrencyGlyph = ""; // Bank
    private const string FallbackGlyph = ""; // Market
    private const string LinkGlyph = "";     // Link (attribution row)

    // Elbstream's neutral identifiers match our DomainInstrument.Symbol directly (AAPL, BTC), so no
    // provider-specific symbol translation is needed. format=png keeps icons raster (host SVG support is
    // uncertain); size defaults to 100px, fine for a list icon.
    public static IconInfo Resolve(string symbol, AssetCategory category) => category switch
    {
        AssetCategory.Stock => new IconInfo($"{Base}/symbol/{Uri.EscapeDataString(symbol)}?format=png"),
        AssetCategory.Crypto => new IconInfo($"{Base}/crypto/{Uri.EscapeDataString(symbol)}?format=png"),
        AssetCategory.Currency => ResolveCurrencyIcon(symbol),
        _ => new IconInfo(FallbackGlyph),
    };

    // An FX pair (EURUSD) → its BASE currency's country flag (EUR → "eu"). Unknown currencies fall back
    // to the generic bank glyph rather than a 404 empty slot.
    private static IconInfo ResolveCurrencyIcon(string symbol) =>
        symbol is { Length: 6 } && CurrencyCountry.TryGetValue(symbol[..3].ToUpperInvariant(), out var iso2)
            ? new IconInfo($"{Base}/country/{iso2}?format=png")
            : new IconInfo(CurrencyGlyph);

    // Currency code → ISO-3166 alpha-2 country code for Elbstream's flag CDN. Covers the
    // ECB-published currencies Frankfurter can serve; extend as new pairs are added to the catalog.
    private static readonly Dictionary<string, string> CurrencyCountry = new(StringComparer.Ordinal)
    {
        ["USD"] = "us", ["EUR"] = "eu", ["GBP"] = "gb", ["JPY"] = "jp", ["CHF"] = "ch",
        ["AUD"] = "au", ["CAD"] = "ca", ["NZD"] = "nz", ["CNY"] = "cn", ["HKD"] = "hk",
        ["SGD"] = "sg", ["SEK"] = "se", ["NOK"] = "no", ["DKK"] = "dk", ["PLN"] = "pl",
        ["ZAR"] = "za", ["MXN"] = "mx", ["INR"] = "in", ["BRL"] = "br", ["KRW"] = "kr",
    };

    public static IconInfo Resolve(DomainInstrument instrument) => Resolve(instrument.Symbol, instrument.Category);

    public static IconInfo Resolve(UiQuote quote) => Resolve(quote.Symbol, quote.Category);

    // The Elbstream attribution row, appended to every list page that shows logos. Clickable → opens
    // elbstream.com. A list-item title renders at the host's standard (well above 12pt) size, satisfying
    // Elbstream's "clearly visible, min 12pt" requirement.
    public static IListItem AttributionRow() =>
        new ListItem(new OpenUrlCommand("https://elbstream.com", Resources.Attribution_OpenName))
        {
            Title = Resources.Attribution_Title,
            Subtitle = Resources.Attribution_Subtitle,
            Icon = new IconInfo(LinkGlyph),
        };
}

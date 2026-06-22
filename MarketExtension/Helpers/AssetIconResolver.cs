using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Resolves an instrument's identity to a row/dock IconInfo. This is the single place the logo-URL
// convention lives. Logos come from Elbstream's keyless CDN (https://elbstream.com), addressed
// directly by symbol — so there's no API call, no caching layer, and no DTO: new IconInfo(url) just
// hands the URL to the CmdPal host, which fetches the image itself.
//
// Categories without a logo source (Currency today) and unknown categories fall back to a per-category
// Segoe MDL2 glyph. Elbstream is free WITH ATTRIBUTION — every surface that shows a logo must carry the
// credit (see AttributionRow + its placements in the priced/search pages and the detail card).
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
        AssetCategory.Currency => new IconInfo(CurrencyGlyph),
        _ => new IconInfo(FallbackGlyph),
    };

    public static IconInfo Resolve(DomainInstrument instrument) => Resolve(instrument.Symbol, instrument.Category);

    public static IconInfo Resolve(UiQuote quote) => Resolve(quote.Symbol, quote.Category);

    // The Elbstream attribution row, appended to every list page that shows logos. Clickable → opens
    // elbstream.com. A list-item title renders at the host's standard (well above 12pt) size, satisfying
    // Elbstream's "clearly visible, min 12pt" requirement.
    public static IListItem AttributionRow() =>
        new ListItem(new OpenUrlCommand("https://elbstream.com", "Open Elbstream"))
        {
            Title = "Logos provided by Elbstream",
            Subtitle = "Logo images via elbstream.com",
            Icon = new IconInfo(LinkGlyph),
        };
}

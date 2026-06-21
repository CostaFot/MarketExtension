using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// The shared per-symbol detail screen, reached by pressing Enter on any instrument row (Search,
// Watchlist, Favorites). For now it's a PLACEHOLDER: it shows the instrument's identity and a
// "chart coming soon" note. It will grow into the real detail screen — a live price chart plus the
// list-management actions (add / remove / favorite) that today live as the rows' context items —
// per the "Symbol detail + live chart" roadmap in CLAUDE.md.
//
// Built on the toolkit's ContentPage so the eventual adaptive-card / SVG-sparkline version is a
// straight in-place upgrade (swap MarkdownContent for a FormContent). No async, no JSON, no
// reflection → AOT/trim-safe and nothing to register.
internal sealed partial class SymbolDetailPage : ContentPage
{
    private readonly DomainInstrument _instrument;

    public SymbolDetailPage(DomainInstrument instrument)
    {
        _instrument = instrument;
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = $"{instrument.Symbol} · {instrument.Name}";
        Name = "View details";
    }

    public override IContent[] GetContent() =>
    [
        new MarkdownContent(
            $"# {_instrument.Symbol}\n\n" +
            $"**{_instrument.Name}** · {CategoryLabel(_instrument.Category)}\n\n" +
            "---\n\n" +
            "📈 **Live price chart coming soon.**\n\n" +
            "List management (add / remove / favorite) will move here too."),
    ];

    private static string CategoryLabel(AssetCategory category) => category switch
    {
        AssetCategory.Stock => "Stock",
        AssetCategory.Crypto => "Crypto",
        AssetCategory.Currency => "Currency",
        _ => "Other",
    };
}

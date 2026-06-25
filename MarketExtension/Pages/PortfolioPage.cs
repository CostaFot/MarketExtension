using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// PLACEHOLDER. Reached from the Markets hub. The real screen — actual holdings (quantity + cost
// basis) with total market value and daily P&L, plus its own dock band — is designed in CLAUDE.md
// under "Portfolio screen + dock band (future wishlist)" but not built yet. For now this is a plain
// static Markdown "coming soon" card (modeled on DataSourcesPage): no data fetch, no providers, no
// lifecycle. When it's built, this becomes a PricedListPage subclass; until then it just explains
// what's coming so the hub row isn't a dead end.
internal sealed partial class PortfolioPage : ContentPage
{
    private readonly MarkdownContent _content = new(
        """
        # Portfolio

        **Coming soon.**

        This screen will track your actual holdings — not just symbols you watch,
        but how much of each you hold — and roll them up into:

        - **Total market value** across everything you own
        - **Daily profit & loss**, in both dollars and percent
        - A per-holding breakdown, each priced live

        It'll share the same live price refresh as your Watchlist and Favorites,
        and get its own dock band showing the one-line summary at a glance.

        For now this is a placeholder. Use **Watchlist** and **Favorites** to
        track instruments in the meantime.
        """);

    public PortfolioPage()
    {
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = "Portfolio";
        Name = "Open";
    }

    public override IContent[] GetContent() => [_content];
}

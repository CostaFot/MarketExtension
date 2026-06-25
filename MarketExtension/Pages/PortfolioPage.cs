using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the user's portfolio holdings, priced live, with a totals summary pinned on top.
// Reached from the Markets hub. Subclasses PricedListPage (like Watchlist/Favorites) to inherit all of
// its caching / polling / reconcile / keep-last-good plumbing — it just observes PortfolioStore.Instruments
// (the symbols to price) instead of a watchlist subset.
//
// Each row shows the holding (symbol · quantity) with its market value and today's P&L; Enter opens the
// shared SymbolDetailPage, which is where holdings are added/edited/removed (Add to Portfolio / Edit
// holding / Remove), consistent with every other list. The quantity for a row is read from PortfolioStore
// at render time — the same way WatchlistPage reads IsFavorite — and the totals summary is built from the
// full priced set via the LeadingRows hook. A quantity-only edit re-emits PortfolioStore.Instruments
// (default-equality flow), so the base re-renders with no fetch and the new quantity + total show at once.
internal sealed partial class PortfolioPage : PricedListPage
{
    private const string PortfolioGlyph = "\uE825"; // Segoe MDL2 Bank

    public PortfolioPage(MarketRepository repository) : base(repository, PortfolioStore.Instance.Instruments)
    {
        Title = "Markets Portfolio";
        Name = "Open";
        PlaceholderText = "Filter your holdings...";
    }

    // The totals summary, pinned above the holdings. Built from the FULL priced set zipped with the current
    // quantities, so it always reflects the WHOLE portfolio even while the search box filters rows below.
    protected override IEnumerable<IListItem> LeadingRows(IReadOnlyList<UiQuote> pricedQuotes)
    {
        var positions = new List<UiPosition>();
        foreach (var q in pricedQuotes)
            if (PortfolioStore.Instance.TryGetQuantity(q.Symbol, out var qty))
                positions.Add(UiPosition.From(q.Source, qty));

        if (positions.Count == 0)
            return [];

        var portfolio = UiPortfolio.From(positions);
        return
        [
            new ListItem(new NoOpCommand())
            {
                Title = $"Portfolio {portfolio.FormatTotalValue()}",
                Subtitle = portfolio.FormatTotalChange(),
                Icon = new IconInfo(PortfolioGlyph),
            },
        ];
    }

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);
        PortfolioStore.Instance.TryGetQuantity(q.Symbol, out var qty);
        var position = UiPosition.From(q.Source, qty);

        return new ListItem(new SymbolDetailPage(instrument, Repository))
        {
            Title = position.FormatHolding(),
            Subtitle = $"{position.FormatMarketValue()}   {position.FormatDailyPnL()}",
            Icon = AssetIconResolver.Resolve(q),
        };
    }

    protected override IListItem[] EmptyState() =>
    [
        new ListItem(new NoOpCommand())
        {
            Title = "No holdings yet",
            Subtitle = "Open any instrument's detail page and choose Add to Portfolio to start tracking holdings",
        },
    ];
}

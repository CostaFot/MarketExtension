using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using MarketExtension.Properties;

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
//
// Multi-currency: holdings priced in various native currencies are converted into the user's
// PortfolioCurrency setting. Conversion rates are an async FX fetch (CurrencyConverter / Frankfurter), but
// GetItems is synchronous — so the rates are PRIMED off the price-load path (OnPriceCacheUpdated, the base's
// post-update hook) and read from the converter's cache while rendering. Until a rate lands a row shows its
// native value only and sits out of the total; a holding in a currency the FX provider can't convert stays
// that way (surfaced as "N not converted" on the summary).
internal sealed partial class PortfolioPage : PricedListPage
{
    private const string PortfolioGlyph = ""; // Segoe MDL2 Bank

    public PortfolioPage(MarketRepository repository) : base(repository, PortfolioStore.Instance.Instruments)
    {
        Title = Resources.Page_Portfolio_Title;
        Name = Resources.Action_Open;
        PlaceholderText = Resources.Portfolio_Placeholder;
    }

    // The totals summary, pinned above the holdings. Built from the FULL priced set zipped with the current
    // quantities, so it always reflects the WHOLE portfolio even while the search box filters rows below.
    protected override IEnumerable<IListItem> LeadingRows(IReadOnlyList<UiQuote> pricedQuotes)
    {
        var preferred = MarketSettingsManager.Instance.PortfolioCurrency;
        var positions = new List<UiPosition>();
        foreach (var q in pricedQuotes)
            if (PortfolioStore.Instance.GetPosition(q.Symbol) is { } held)
                positions.Add(MakePosition(q.Source, held.Quantity, held.CostBasis, preferred));

        if (positions.Count == 0)
            return [];

        var portfolio = UiPortfolio.From(positions, preferred);
        return
        [
            new ListItem(new NoOpCommand())
            {
                Title = Strings.Format(Resources.Portfolio_TotalsRow_Title, portfolio.FormatTotalValue()),
                Subtitle = portfolio.FormatTotalChange() + portfolio.FormatTotalReturnNote() + portfolio.FormatUnconvertedNote(),
                Icon = new IconInfo(PortfolioGlyph),
            },
        ];
    }

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);
        var held = PortfolioStore.Instance.GetPosition(q.Symbol);
        var position = MakePosition(q.Source, held?.Quantity ?? 0m, held?.CostBasis, MarketSettingsManager.Instance.PortfolioCurrency);

        // Daily P&L always; total return appended ("Total ▲ …") only when a cost basis is recorded.
        var subtitle = $"{position.FormatValue()}   {position.FormatDailyPnL()}";
        var totalReturn = position.FormatTotalReturn();
        if (totalReturn.Length > 0)
            subtitle += "   " + Strings.Format(Resources.Portfolio_Row_TotalReturnPrefix, totalReturn);

        return new ListItem(new SymbolDetailPage(instrument, Repository))
        {
            Title = position.FormatHolding(),
            Subtitle = subtitle,
            Icon = AssetIconResolver.Resolve(q),
        };
    }

    // Build a UiPosition, looking up the (cached) native→preferred FX rate. A null rate means "not known
    // yet / not convertible" — the position renders native-only and stays out of the total. costBasis is the
    // per-unit price paid (null when none recorded), carried through for total-return reporting.
    private static UiPosition MakePosition(DomainQuote quote, decimal qty, decimal? costBasis, string preferred)
    {
        var rate = CurrencyConverter.Instance.TryGetRate(quote.Currency, preferred);
        return UiPosition.From(quote, qty, preferred, rate, costBasis);
    }

    // After each price update, ensure the FX rates for every native currency now present are fetched into
    // the converter's cache, then re-render so the converted values + total appear. Runs off the UI/render
    // path; the converter skips currencies it already has fresh, so a steady portfolio does no extra network.
    protected override void OnPriceCacheUpdated()
    {
        var preferred = MarketSettingsManager.Instance.PortfolioCurrency;
        var natives = SnapshotPricedQuotes()
            .Where(q => q.IsValid)
            .Select(q => q.Source.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (natives.Length == 0)
            return;

        Task.Run(async () =>
        {
            await CurrencyConverter.Instance.PrimeAsync(preferred, natives);
            RaiseItemsChanged(0);
        });
    }

    protected override IListItem[] EmptyState() =>
    [
        new ListItem(new NoOpCommand())
        {
            Title = Resources.Portfolio_Empty_Title,
            Subtitle = Resources.Portfolio_Empty_Subtitle,
        },
    ];
}

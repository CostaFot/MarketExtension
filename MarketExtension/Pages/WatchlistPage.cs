using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the instruments the user tracks, priced live and grouped by asset class. A ★
// prefix marks rows that are also favorited. Enter opens the row's SymbolDetailPage, which is the
// single place to remove it or toggle its favorite mark. The row carries no context actions.
internal sealed partial class WatchlistPage : PricedListPage
{
    public WatchlistPage(MarketRepository repository) : base(repository, WatchlistStore.Instance.Watchlist)
    {
        Title = Strings.Get("Page_Watchlist_Title");
        Name = Strings.Get("Action_Open");
        PlaceholderText = Strings.Get("Watchlist_Placeholder");
    }

    // A favorite toggled here doesn't change the watchlist set, so observe the Favorites flow too and
    // re-render when it changes — that keeps the ★ marks current after a Ctrl+Enter toggle.
    protected override IEnumerable<StateFlow<IReadOnlyList<DomainInstrument>>> RelistTriggers
        => [WatchlistStore.Instance.Favorites];

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);
        var isFavorite = WatchlistStore.Instance.IsFavorite(q.Symbol);

        return new ListItem(new SymbolDetailPage(instrument, Repository))
        {
            Title = (isFavorite ? "★ " : "") + $"{q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            Section = SectionLabel(q.Category),
            Icon = AssetIconResolver.Resolve(q),
        };
    }

    protected override IListItem[] EmptyState() =>
    [
        new ListItem(new NoOpCommand())
        {
            Title = Strings.Get("Watchlist_Empty_Title"),
            Subtitle = Strings.Get("Watchlist_Empty_Subtitle"),
        },
    ];

    private static string SectionLabel(AssetCategory category) => category switch
    {
        AssetCategory.Stock => Strings.Get("Section_Stocks"),
        AssetCategory.Crypto => Strings.Get("Section_Crypto"),
        AssetCategory.Currency => Strings.Get("Section_Currency"),
        _ => Strings.Get("Section_Other"),
    };
}

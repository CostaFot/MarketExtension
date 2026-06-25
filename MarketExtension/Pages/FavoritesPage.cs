using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using MarketExtension.Properties;

namespace MarketExtension;

// Top-level screen: the curated favorites — the same set the dock band shows — priced live. Enter
// opens the row's SymbolDetailPage, which is the single place to remove it from favorites (leaving
// it on the watchlist if it's tracked there). The row carries no context actions.
internal sealed partial class FavoritesPage : PricedListPage
{
    public FavoritesPage(MarketRepository repository) : base(repository, WatchlistStore.Instance.Favorites)
    {
        Title = Resources.Page_Favorites_Title;
        Name = Resources.Action_Open;
        PlaceholderText = Resources.Favorites_Placeholder;
    }

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);

        return new ListItem(new SymbolDetailPage(instrument, Repository))
        {
            Title = $"★ {q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            Icon = AssetIconResolver.Resolve(q),
        };
    }

    protected override IListItem[] EmptyState() =>
    [
        new ListItem(new NoOpCommand())
        {
            Title = Resources.Favorites_Empty_Title,
            Subtitle = Resources.Favorites_Empty_Subtitle,
        },
    ];
}

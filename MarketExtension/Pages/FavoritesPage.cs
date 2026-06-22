using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the curated favorites — the same set the dock band shows — priced live. Enter
// opens the row's SymbolDetailPage, which is the single place to remove it from favorites (leaving
// it on the watchlist if it's tracked there). The row carries no context actions.
internal sealed partial class FavoritesPage : PricedListPage
{
    public FavoritesPage(MarketRepository repository) : base(repository, WatchlistStore.Instance.Favorites)
    {
        Title = "Markets Favorites";
        Name = "Open";
        PlaceholderText = "Filter your favorites...";
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
            Title = "No favorites yet",
            Subtitle = "Star instruments from the More menu in Markets Search or your Watchlist",
        },
    ];
}

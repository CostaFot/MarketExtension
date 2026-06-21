using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the curated favorites — the same set the dock band shows — priced live. Enter
// opens the row's SymbolDetailPage; removing from favorites (Ctrl+Enter — the first MoreCommands
// context item) lives in the More menu. Removing here leaves the instrument on the watchlist if
// it's tracked there. This and the Watchlist screen's toggle are the only places that unstar.
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

        return new ListItem(new SymbolDetailPage(instrument))
        {
            Title = $"★ {q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            MoreCommands =
            [
                new CommandContextItem(new RemoveFromFavoritesCommand(instrument)),
                new CommandContextItem(new CopyTextCommand(q.FormatPrice())) { Title = "Copy price" },
            ],
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

using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the curated favorites — the same set the dock band shows — priced live. Enter
// removes the row from favorites (it stays on the watchlist if it's tracked there). This is the
// only place, besides the Watchlist screen's Ctrl+Enter, that unstars an instrument.
internal sealed partial class FavoritesPage : PricedListPage
{
    public FavoritesPage(MarketRepository repository) : base(repository)
    {
        Title = "Markets Favorites";
        Name = "Open";
        PlaceholderText = "Filter your favorites...";
    }

    protected override IReadOnlyList<DomainInstrument> InstrumentsToPrice()
        => WatchlistStore.Instance.Favorites;

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);

        return new ListItem(new RemoveFromFavoritesCommand(instrument, () => RaiseItemsChanged(0)))
        {
            Title = $"★ {q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(q.FormatPrice())) { Title = "Copy price" },
            ],
        };
    }

    protected override IListItem[] EmptyState() =>
    [
        new ListItem(new NoOpCommand())
        {
            Title = "No favorites yet",
            Subtitle = "Star instruments from Markets Search, or from your Watchlist with Ctrl+Enter",
        },
    ];
}

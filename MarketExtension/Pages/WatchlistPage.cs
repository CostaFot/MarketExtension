using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the instruments the user tracks, priced live and grouped by asset class. A ★
// prefix marks rows that are also favorited. Enter removes the row from the watchlist; Ctrl+Enter
// (the first MoreCommands context item) toggles its favorite mark so the user can curate the dock
// without going back to search.
internal sealed partial class WatchlistPage : PricedListPage
{
    public WatchlistPage(MarketRepository repository) : base(repository)
    {
        Title = "Markets Watchlist";
        Name = "Open";
        PlaceholderText = "Filter your watchlist...";
    }

    protected override IReadOnlyList<DomainInstrument> InstrumentsToPrice()
        => WatchlistStore.Instance.Watchlist;

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);
        var isFavorite = WatchlistStore.Instance.IsFavorite(q.Symbol);

        return new ListItem(new RemoveFromWatchlistCommand(instrument, () => RaiseItemsChanged(0)))
        {
            Title = (isFavorite ? "★ " : "") + $"{q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            Section = SectionLabel(q.Category),
            MoreCommands =
            [
                new CommandContextItem(new ToggleFavoriteCommand(instrument, () => RaiseItemsChanged(0)))
                {
                    Title = isFavorite ? "Remove from Favorites" : "Add to Favorites",
                },
                new CommandContextItem(new CopyTextCommand(q.FormatPrice())) { Title = "Copy price" },
            ],
        };
    }

    protected override IListItem[] EmptyState() =>
    [
        new ListItem(new NoOpCommand())
        {
            Title = "Your watchlist is empty",
            Subtitle = "Use Markets Search to look up instruments, then press Enter to add them",
        },
    ];

    private static string SectionLabel(AssetCategory category) => category switch
    {
        AssetCategory.Stock => "Stocks",
        AssetCategory.Crypto => "Crypto",
        AssetCategory.Currency => "Currency",
        _ => "Other",
    };
}

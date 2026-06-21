using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Top-level screen: the instruments the user tracks, priced live and grouped by asset class. A ★
// prefix marks rows that are also favorited. Enter opens the row's SymbolDetailPage; removing from
// the watchlist (Ctrl+Enter — the first MoreCommands context item) and toggling its favorite mark
// live in the More menu, so the user can curate the dock without going back to search.
internal sealed partial class WatchlistPage : PricedListPage
{
    public WatchlistPage(MarketRepository repository) : base(repository, WatchlistStore.Instance.Watchlist)
    {
        Title = "Markets Watchlist";
        Name = "Open";
        PlaceholderText = "Filter your watchlist...";
    }

    // A favorite toggled here doesn't change the watchlist set, so observe the Favorites flow too and
    // re-render when it changes — that keeps the ★ marks current after a Ctrl+Enter toggle.
    protected override IEnumerable<StateFlow<IReadOnlyList<DomainInstrument>>> RelistTriggers
        => [WatchlistStore.Instance.Favorites];

    protected override IListItem BuildRow(UiQuote q)
    {
        var instrument = new DomainInstrument(q.Symbol, q.Name, q.Category);
        var isFavorite = WatchlistStore.Instance.IsFavorite(q.Symbol);

        return new ListItem(new SymbolDetailPage(instrument))
        {
            Title = (isFavorite ? "★ " : "") + $"{q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            Section = SectionLabel(q.Category),
            MoreCommands =
            [
                new CommandContextItem(new RemoveFromWatchlistCommand(instrument)),
                new CommandContextItem(new ToggleFavoriteCommand(instrument))
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
            Subtitle = "Use Markets Search to look up instruments, then add them from the More menu",
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

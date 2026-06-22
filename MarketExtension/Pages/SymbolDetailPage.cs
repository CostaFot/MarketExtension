using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// The shared per-symbol detail screen, reached by pressing Enter on any instrument row (Search,
// Watchlist, Favorites). The chart is still a PLACEHOLDER, but list management lives here now: the
// page's command bar carries the add/remove-watchlist and add/remove-favorite actions (Enter and
// Ctrl+Enter respectively), with the body showing current membership. This is the screen that owns
// list management going forward — the rows' context-menu copies are a transitional convenience.
// Built on the toolkit's ContentPage so the eventual SVG-sparkline version is a straight in-place
// upgrade (swap MarkdownContent for a FormContent).
//
// Reactive like the rest of the app (see Helpers/StateFlow.cs + the "prefer observable data flow"
// convention): it subscribes to WatchlistStore's two membership flows while visible — the
// INotifyItemsChanged add/remove lifecycle, the same seam the list pages use — and rebuilds its
// Commands + body on every change. So toggling watchlist/favorite here (the commands KeepOpen) flips
// the buttons in place, and a change made elsewhere is reflected too. No manual refresh callback.
internal sealed partial class SymbolDetailPage : ContentPage, INotifyItemsChanged
{
    private readonly DomainInstrument _instrument;
    private readonly List<IDisposable> _subscriptions = [];

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Replay-on-subscribe paints the initial command bar/body; later pushes flip them in place.
            _subscriptions.Add(WatchlistStore.Instance.Watchlist.Subscribe(_ => RefreshState()));
            _subscriptions.Add(WatchlistStore.Instance.Favorites.Subscribe(_ => RefreshState()));
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public SymbolDetailPage(DomainInstrument instrument)
    {
        _instrument = instrument;
        // Stable, unique per symbol — also lets this page stand in as a Dock band item's command,
        // where a non-empty Id is required.
        Id = $"com.costafotiadis.market.detail.{instrument.Symbol}";
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = $"{instrument.Symbol} · {instrument.Name}";
        Name = "View details";
        Commands = BuildCommands(); // initial bar; refreshed again on subscribe and on every change
    }

    public override IContent[] GetContent() => [new MarkdownContent(BuildBody())];

    // Push the current membership into both the command bar (set Commands → host re-reads it) and the
    // body (RaiseItemsChanged → host re-calls GetContent).
    private void RefreshState()
    {
        Commands = BuildCommands();
        RaiseItemsChanged();
    }

    // The two list-management actions, labelled add/remove for the instrument's current state. The
    // first is the page's primary command (Enter), the second the secondary (Ctrl+Enter).
    private IContextItem[] BuildCommands()
    {
        var inWatchlist = WatchlistStore.Instance.IsInWatchlist(_instrument.Symbol);
        var isFavorite = WatchlistStore.Instance.IsFavorite(_instrument.Symbol);

        IContextItem watchlist = inWatchlist
            ? new CommandContextItem(new RemoveFromWatchlistCommand(_instrument))
            : new CommandContextItem(new AddToWatchlistCommand(_instrument));

        IContextItem favorite = isFavorite
            ? new CommandContextItem(new RemoveFromFavoritesCommand(_instrument))
            : new CommandContextItem(new AddToFavoritesCommand(_instrument));

        return [watchlist, favorite];
    }

    private string BuildBody()
    {
        var inWatchlist = WatchlistStore.Instance.IsInWatchlist(_instrument.Symbol);
        var isFavorite = WatchlistStore.Instance.IsFavorite(_instrument.Symbol);
        var status = (inWatchlist, isFavorite) switch
        {
            (true, true) => "On your watchlist · ★ Favorite",
            (true, false) => "On your watchlist",
            (false, true) => "★ Favorite",
            (false, false) => "Not tracked yet",
        };

        return
            $"# {_instrument.Symbol}\n\n" +
            $"**{_instrument.Name}** · {CategoryLabel(_instrument.Category)}\n\n" +
            $"_{status}_\n\n" +
            "---\n\n" +
            "📈 **Live price chart coming soon.**";
    }

    private static string CategoryLabel(AssetCategory category) => category switch
    {
        AssetCategory.Stock => "Stock",
        AssetCategory.Crypto => "Crypto",
        AssetCategory.Currency => "Currency",
        _ => "Other",
    };
}

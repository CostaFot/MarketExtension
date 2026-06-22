using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Backs a Command Palette Dock band: a strip showing up to MaxDockFavorites favorited
// instruments as ticker buttons (e.g. "AAPL ▲ +1.20%"); clicking one opens its SymbolDetailPage.
// Returned from MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
//
// Because the band's command is an IListPage, the host renders each item from GetItems() as
// its own button within the one band (see reference/dock-support.md). We use the project's
// INotifyItemsChanged on-load refresh so the band re-reads favorites + quotes every time it
// becomes visible. Live polling while visible (a timer) + a real data source come with the
// API phase — see reference/dock-support.md for the OnLoad lifecycle to add then.
internal sealed partial class FavoritesDockPage : ListPage, INotifyItemsChanged
{
    private const int MaxDockFavorites = 3;

    private readonly MarketRepository _repository;
    private UiQuote[]? _quotes;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private IDisposable? _subscription;
    private IDisposable? _pollSubscription;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the favorites flow while the band is visible: its replay paints the band the moment
            // it opens, and any later star/unstar from a palette page refreshes it at once (no waiting
            // for a reopen). Disposed in `remove` so a hidden band does no work and doesn't leak.
            _subscription = WatchlistStore.Instance.Favorites.Subscribe(_ => RefreshQuotes());
            // Live polling: each tick silently re-prices favorites in place (no spinner). replay:false so
            // becoming visible doesn't double-fetch — the favorites subscription above already paints.
            _pollSubscription = PollTicker.Instance.Subscribe(_ => PollRefresh(), replayOnSubscribe: false);
            Log.Info("Poll", $"Dock: started polling [{string.Join(", ", WatchlistStore.Instance.Favorites.Value.Select(i => i.Symbol))}]");
        }
        remove
        {
            _itemsChanged -= value;
            _subscription?.Dispose();
            _subscription = null;
            _pollSubscription?.Dispose();
            _pollSubscription = null;
            Log.Info("Poll", "Dock: stopped polling");
        }
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public FavoritesDockPage(MarketRepository repository)
    {
        _repository = repository;
        Id = "com.costafotiadis.market.dock.favorites"; // dock bands require a non-empty command Id
        Title = "Markets";
        Icon = new IconInfo("https://github.com/favicon.ico");
    }

    public override IListItem[] GetItems()
    {
        if (_quotes is null)
            return [];

        var favorites = _quotes.Take(MaxDockFavorites).ToArray();

        if (favorites.Length == 0)
        {
            return [new ListItem(new NoOpCommand { Id = "com.costafotiadis.market.dock.empty" })
            {
                Title = "No favorites yet",
                Subtitle = "Star instruments from Markets Search or your Watchlist",
            }];
        }

        return favorites
            .Select(q => (IListItem)new ListItem(
                new SymbolDetailPage(new DomainInstrument(q.Symbol, q.Name, q.Category), _repository))
            {
                Title = $"{q.Symbol} {q.FormatChange()}",
                Subtitle = q.FormatPrice(),
            })
            .ToArray();
    }

    internal void RefreshQuotes()
    {
        _quotes = null;
        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(LoadQuotes);
    }

    // Live-poll refresh: re-price favorites WITHOUT clearing _quotes or showing a spinner, so the current
    // prices stay on the band until LoadQuotes swaps the new ones in (no flicker). Skips work before the
    // first paint — the favorites subscription already has a load in flight then.
    internal void PollRefresh()
    {
        if (_quotes is null)
            return;
        Log.Info("Poll", $"Dock: re-pricing favorites [{string.Join(", ", WatchlistStore.Instance.Favorites.Value.Select(i => i.Symbol))}]");
        Task.Run(LoadQuotes);
    }

    private async Task LoadQuotes()
    {
        // The dock shows only favorites — price exactly that subset (a snapshot of the flow's value).
        _quotes = [.. (await _repository.GetQuotesAsync(WatchlistStore.Instance.Favorites.Value)).Select(UiQuote.From)];
        IsLoading = false;
        RaiseItemsChanged(0);
    }
}

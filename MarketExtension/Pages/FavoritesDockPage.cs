using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Backs a Command Palette Dock band: a strip showing every favorited
// instrument as ticker buttons (e.g. "AAPL ▲ +1.20%"); clicking one opens its SymbolDetailPage.
// Returned from MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
//
// Because the band's command is an IListPage, the host renders each item from GetItems() as
// its own button within the one band (see reference/dock-support.md). We use the project's
// INotifyItemsChanged on-load refresh so the band re-reads favorites + quotes every time it
// becomes visible. Live polling while visible (a timer) + a real data source come with the
// API phase — see reference/dock-support.md for the OnLoad lifecycle to add then.
internal sealed partial class FavoritesDockPage : ListPage, INotifyItemsChanged
{
    private readonly MarketRepository _repository;
    private UiQuote[]? _quotes;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private IDisposable? _subscription;
    private IDisposable? _pollSubscription;
    private IDisposable? _demoModeSubscription;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the favorites flow while the band is visible: its replay paints the band the moment
            // it opens, and any later star/unstar from a palette page refreshes it at once (no waiting
            // for a reopen). Disposed in `remove` so a hidden band does no work and doesn't leak.
            _subscription = WatchlistStore.Instance.Favorites.Subscribe(_ => RefreshQuotes());
            // Live polling: each tick silently re-prices favorites in place (no spinner). The ticker is a
            // pure event stream (no replay), so becoming visible doesn't double-fetch — the favorites
            // subscription above already paints.
            _pollSubscription = PollTicker.Subscribe(PollRefresh);
            // Demo mode flips the data source — re-price from the new one at once if the band is open. A
            // hidden band already re-fetches on its next open (the favorites-flow replay calls RefreshQuotes),
            // so this only needs to cover a currently-visible/pinned band. replay:false — opening already
            // paints via the favorites subscription above.
            _demoModeSubscription = MarketSettingsManager.Instance.DemoModeChanged
                .Subscribe(_ => RefreshQuotes(), replayOnSubscribe: false);
            Log.Info("Poll", $"Dock: started polling [{string.Join(", ", WatchlistStore.Instance.Favorites.Value.Select(i => i.Symbol))}]");
        }
        remove
        {
            _itemsChanged -= value;
            _subscription?.Dispose();
            _subscription = null;
            _pollSubscription?.Dispose();
            _pollSubscription = null;
            _demoModeSubscription?.Dispose();
            _demoModeSubscription = null;
            Log.Info("Poll", "Dock: stopped polling");
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public FavoritesDockPage(MarketRepository repository)
    {
        _repository = repository;
        Id = "com.costafotiadis.market.dock.favorites"; // dock bands require a non-empty command Id
        Title = "Markets";
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
    }

    public override IListItem[] GetItems()
    {
        if (_quotes is null)
            return [];

        var favorites = _quotes;

        if (favorites.Length == 0)
        {
            return [new ListItem(new NoOpCommand { Id = "com.costafotiadis.market.dock.empty" })
            {
                Title = "No favorites yet",
                Subtitle = "Open an instrument and star it from its detail page",
            }];
        }

        return favorites
            .Select(q => (IListItem)new ListItem(
                new SymbolDetailPage(new DomainInstrument(q.Symbol, q.Name, q.Category), _repository))
            {
                Title = $"{q.Symbol} {q.FormatChange()}",
                Subtitle = q.FormatPrice(),
                Icon = AssetIconResolver.Resolve(q),
            })
            .ToArray();
    }

    internal void RefreshQuotes()
    {
        _quotes = null;
        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(() => LoadQuotes(silent: false));
    }

    // Live-poll refresh: re-price favorites WITHOUT clearing _quotes or showing a spinner, so the current
    // prices stay on the band until LoadQuotes swaps the new ones in (no flicker). Skips work before the
    // first paint — the favorites subscription already has a load in flight then. `silent: true` also keeps
    // a transient bad poll from blanking a good price (see LoadQuotes).
    internal void PollRefresh()
    {
        if (_quotes is null)
            return;
        Log.Info("Poll", $"Dock: re-pricing favorites [{string.Join(", ", WatchlistStore.Instance.Favorites.Value.Select(i => i.Symbol))}]");
        Task.Run(() => LoadQuotes(silent: true));
    }

    // `silent` = a background poll (no spinner): in that mode, don't let a transient bad result (e.g. a
    // Finnhub 429 mapped to an invalid quote) overwrite a price that was fine a moment ago. This mirrors
    // PricedListPage.LoadQuotes' keep-last-good guard — without it a single failed poll would replace every
    // UiQuote with an invalid one and the band would blank (symbol-only buttons) until the extension is
    // reloaded, because a pinned dock never re-fetches except on a poll tick.
    private async Task LoadQuotes(bool silent)
    {
        // The dock shows only favorites — price exactly that subset (a snapshot of the flow's value).
        var prior = _quotes; // on-screen prices, for the keep-last-good merge
        IEnumerable<UiQuote> fetched =
            (await _repository.GetQuotesAsync(WatchlistStore.Instance.Favorites.Value)).Select(UiQuote.From);

        if (silent && prior is not null)
        {
            var lastGood = prior
                .Where(q => q.IsValid)
                .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            fetched = fetched.Select(q =>
                !q.IsValid && lastGood.TryGetValue(q.Symbol, out var good) ? good : q);
        }

        _quotes = [.. fetched];
        IsLoading = false;
        RaiseItemsChanged(0);
    }
}

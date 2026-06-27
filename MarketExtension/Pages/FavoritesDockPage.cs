using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Backs a Command Palette Dock band: a strip showing every favorited
// instrument as ticker buttons (e.g. "AAPL ▲ +1.20%"); clicking one opens its SymbolDetailPage.
// Returned from MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
//
// Because the band's command is an IListPage, the host renders each item from GetItems() as
// its own button within the one band (see reference/dock-support.md). We use the project's
// INotifyItemsChanged on-load refresh so the band re-renders every time it becomes visible.
//
// A PURE OBSERVER of the shared quote cache: while visible it subscribes to the repository's cache-backed
// quote stream for the favorites set (MarketRepository.ObserveQuotes) and renders whatever it emits — and
// nothing else. It does NOT fetch, poll, or handle demo-mode flips itself: the repository owns all of that
// (its single poll loop refreshes the observed set on a timer, and it refills the cache on a source flip),
// so this band can never drift out of sync with any other surface observing the same symbols. Subscribing
// also registers favorites as "observed" so the repository keeps them fresh; disposing on hide unregisters.
//
// Threading: ObserveQuotes delivers via ObserveOn (see MarketRepository) so OnQuotesChanged — and therefore
// RaiseItemsChanged — runs on a pool thread with NO Rx gate lock held. That is what makes this safe: an
// earlier revision delivered synchronously under the CombineLatest/Switch gate, so RaiseItemsChanged's COM
// call into the host re-entered while the gate was held → an STA/gate lock-order deadlock that hung CmdPal.
internal sealed partial class FavoritesDockPage : ListPage, INotifyItemsChanged
{
    private readonly MarketRepository _repository;
    private UiQuote[]? _quotes; // latest cache emission, projected for rendering; null before the first

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    // Subscriptions held in a list (not a single field) so a double-`add` without an intervening `remove`
    // can't orphan a subscription: a single field would be OVERWRITTEN by the second add, losing the first's
    // reference so it's never disposed — and because ObserveQuotes registers its symbols as "observed" on
    // subscribe and only unregisters on dispose, that orphan would pin those symbols to the repository's poll
    // loop forever. Dispose-all-and-clear in `remove` keeps every subscribe balanced. Matches PricedListPage.
    private readonly List<IDisposable> _subscriptions = [];

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the cache-backed quote stream for the favorites set while the band is visible. The
            // membership-aware overload replays the current favorites on subscribe (painting from cache the
            // moment it opens — fetching only symbols not already cached), re-projects on any star/unstar,
            // and re-emits whenever a member quote changes in the cache (so the repository's poll/demo
            // refresh repaints the band). Delivery is off-thread (ObserveOn in the repository), so the first
            // emission lands after this accessor returns — no synchronous RaiseItemsChanged inside the host's
            // subscription. Disposed in `remove` — a hidden band does no work, doesn't leak, and unregisters
            // its symbols so the repository stops polling them.
            _subscriptions.Add(_repository.ObserveQuotes(WatchlistStore.Instance.Favorites).Subscribe(OnQuotesChanged));
            Log.Info("Dock", $"observing favorites [{string.Join(", ", WatchlistStore.Instance.Favorites.Value.Select(i => i.Symbol))}]");
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            Log.Info("Dock", "stopped observing favorites");
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public FavoritesDockPage(MarketRepository repository)
    {
        _repository = repository;
        Id = "com.costafotiadis.market.dock.favorites"; // dock bands require a non-empty command Id
        Title = Resources.Command_Markets;
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
    }

    public override IListItem[] GetItems()
    {
        var favorites = _quotes;
        if (favorites is null)
            return []; // before the first cache emission — nothing to show yet

        if (favorites.Length == 0)
        {
            // An empty list means "no favorites" only when the membership is actually empty; otherwise the
            // prices just haven't landed in the cache yet, so show nothing (the spinner) rather than the
            // empty-state row.
            if (WatchlistStore.Instance.Favorites.Value.Count == 0)
            {
                return [new ListItem(new NoOpCommand { Id = "com.costafotiadis.market.dock.empty" })
                {
                    Title = Resources.Favorites_Empty_Title,
                    Subtitle = Resources.Favorites_Empty_Subtitle,
                }];
            }
            return [];
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

    // A new cache emission for the favorites set: project to UiQuote for rendering and repaint. Runs on a
    // pool thread (ObserveOn) — no Rx gate lock is held here, so RaiseItemsChanged's host call is safe.
    private void OnQuotesChanged(IReadOnlyList<DomainQuote> quotes)
    {
        _quotes = [.. quotes.Select(UiQuote.From)];
        // Spinner only while favorites exist but their prices haven't filled the cache yet.
        IsLoading = _quotes.Length == 0 && WatchlistStore.Instance.Favorites.Value.Count > 0;
        Log.Info("Dock", $"favorites painted: {_quotes.Length} quote(s)");
        RaiseItemsChanged(0);
    }
}

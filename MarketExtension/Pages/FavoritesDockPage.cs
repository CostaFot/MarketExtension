using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Backs a Command Palette Dock band: a strip showing up to MaxDockFavorites favorited
// instruments as ticker buttons (e.g. "AAPL ▲ +1.20%"). Returned from
// MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
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

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshQuotes(); } // fires when the band becomes visible
        remove => _itemsChanged -= value;
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

        var favorites = _quotes
            .Where(q => FavoritesStore.Instance.IsFavorite(q.Symbol))
            .Take(MaxDockFavorites)
            .ToArray();

        if (favorites.Length == 0)
        {
            return [new ListItem(new NoOpCommand { Id = "com.costafotiadis.market.dock.empty" })
            {
                Title = "No favorites yet",
                Subtitle = "Star instruments in the Markets page",
            }];
        }

        return favorites
            .Select(q => (IListItem)new ListItem(
                new CopyTextCommand($"{q.Symbol} {q.FormatPrice()} ({q.FormatChange()})")
                {
                    Id = $"com.costafotiadis.market.dock.copy.{q.Symbol}",
                })
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

    private async Task LoadQuotes()
    {
        _quotes = [.. (await _repository.GetQuotesAsync(InstrumentCatalog.All)).Select(UiQuote.From)];
        IsLoading = false;
        RaiseItemsChanged(0);
    }
}

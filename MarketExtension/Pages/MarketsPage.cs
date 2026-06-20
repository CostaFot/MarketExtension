using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Top-level page: browse market instruments and star favorites.
//
// Structure mirrors reference/pages/AdbExtensionPage.cs — DynamicListPage with the
// INotifyItemsChanged on-load refresh hook (see CLAUDE.md), an async Task.Run load, and a
// trailing Refresh item. Data comes from IMarketDataProvider; today that is the mock, later
// a real API. Favorites are pinned into their own section at the top.
internal sealed partial class MarketsPage : DynamicListPage, INotifyItemsChanged
{
    // Injected so the palette page and the dock band share one repository — the coordinator over
    // market-data providers (see Data/MarketRepository.cs).
    private readonly MarketRepository _repository;
    private UiQuote[]? _quotes;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshQuotes(); } // fires every time the user navigates here
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public MarketsPage(MarketRepository repository)
    {
        _repository = repository;
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = "Markets";
        Name = "Open";
        PlaceholderText = "Search symbol or name...";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (_quotes is null)
            return [];

        var source = string.IsNullOrEmpty(SearchText)
            ? _quotes
            : _quotes.Where(q =>
                q.Symbol.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                q.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (source.Length == 0)
            return [new ListItem(new NoOpCommand()) { Title = "No instruments match 😔" }];

        var items = new List<IListItem>();

        // Favorites first, in their own section (and not repeated in the category sections).
        var favorites = source.Where(q => FavoritesStore.Instance.IsFavorite(q.Symbol)).ToArray();
        foreach (var q in favorites)
            items.Add(BuildItem(q, "★ Favorites"));

        foreach (var category in new[] { AssetCategory.Stock, AssetCategory.Crypto, AssetCategory.Currency })
        {
            var inCategory = source.Where(q => q.Category == category && !FavoritesStore.Instance.IsFavorite(q.Symbol));
            foreach (var q in inCategory)
                items.Add(BuildItem(q, SectionLabel(category)));
        }

        items.Add(new ListItem(new RefreshCommand(this)) { Title = "Refresh 🔄" });

        return items.ToArray();
    }

    private ListItem BuildItem(UiQuote q, string section) =>
        new(new ToggleFavoriteCommand(q.Symbol, () => RaiseItemsChanged(0)))
        {
            Title = $"{q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            Section = section,
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(q.FormatPrice())) { Title = "Copy price" },
            ],
        };

    private static string SectionLabel(AssetCategory category) => category switch
    {
        AssetCategory.Stock => "Stocks",
        AssetCategory.Crypto => "Crypto",
        AssetCategory.Currency => "Currency",
        _ => "Other",
    };

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

    private sealed partial class RefreshCommand : InvokableCommand
    {
        private readonly MarketsPage _page;

        public RefreshCommand(MarketsPage page)
        {
            _page = page;
            Name = "Refresh";
        }

        public override ICommandResult Invoke()
        {
            _page.RefreshQuotes();
            return CommandResult.KeepOpen();
        }
    }
}

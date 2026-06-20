using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Shared base for the two priced list screens (Watchlist, Favorites). Each prices a different
// WatchlistStore subset but otherwise behaves identically: async-load the quotes, render rows,
// re-list on load and on every mutation. Houses the project's INotifyItemsChanged on-load refresh
// hook (see CLAUDE.md) so the list re-prices every time the user navigates to the screen.
//
// Typing only re-filters the already-loaded quotes locally — these screens never hit the network on
// keystroke (only the SearchPage talks to /search, and only on Enter).
internal abstract partial class PricedListPage : DynamicListPage, INotifyItemsChanged
{
    protected readonly MarketRepository Repository;
    protected UiQuote[]? Quotes;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshQuotes(); } // fires every time the user navigates here
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    protected PricedListPage(MarketRepository repository)
    {
        Repository = repository;
        Icon = new IconInfo("https://github.com/favicon.ico");
    }

    // The store subset this screen prices.
    protected abstract IReadOnlyList<DomainInstrument> InstrumentsToPrice();

    // Render one priced row.
    protected abstract IListItem BuildRow(UiQuote quote);

    // Shown when the underlying set is empty.
    protected abstract IListItem[] EmptyState();

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (Quotes is null)
            return [];

        if (Quotes.Length == 0)
            return EmptyState();

        var rows = Quotes.Where(Matches).Select(BuildRow).ToList();
        rows.Add(new ListItem(new RefreshCommand(this)) { Title = "Refresh 🔄" });
        return [.. rows];
    }

    // Instant, no-API filter over the already-loaded quotes.
    private bool Matches(UiQuote q) =>
        string.IsNullOrEmpty(SearchText) ||
        q.Symbol.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
        q.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    internal void RefreshQuotes()
    {
        Quotes = null;
        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(LoadQuotes);
    }

    private async Task LoadQuotes()
    {
        Quotes = [.. (await Repository.GetQuotesAsync(InstrumentsToPrice())).Select(UiQuote.From)];
        IsLoading = false;
        RaiseItemsChanged(0);
    }

    private sealed partial class RefreshCommand : InvokableCommand
    {
        private readonly PricedListPage _page;

        public RefreshCommand(PricedListPage page)
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

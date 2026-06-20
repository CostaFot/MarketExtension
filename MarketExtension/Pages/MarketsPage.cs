using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Top-level page: browse the watchlist, search Finnhub for new instruments, and star favorites.
//
// Structure mirrors reference/pages/AdbExtensionPage.cs — DynamicListPage with the
// INotifyItemsChanged on-load refresh hook (see CLAUDE.md), an async Task.Run load, and a
// trailing Refresh item. The priced list is Watchlist.Instruments() (catalog defaults ∪ pinned).
//
// Search is Enter-only — UpdateSearchText never hits the network (free-tier rate limits). Typing
// only re-filters the already-loaded watchlist locally; the one /search call fires solely when the
// user activates the synthetic "Search Finnhub for …" item (RunSearchCommand). Results are
// price-less identities; adding one pins it and re-prices the watchlist so it shows with a price.
internal sealed partial class MarketsPage : DynamicListPage, INotifyItemsChanged
{
    // Segoe MDL2 "Search" glyph for the online-search action item.
    private const string SearchGlyph = "";

    // Injected so the palette page and the dock band share one repository — the coordinator over
    // market-data providers (see Data/MarketRepository.cs).
    private readonly MarketRepository _repository;
    private UiQuote[]? _quotes;

    // Last executed online search: the results and the exact query they belong to. Results render
    // only while SearchText still equals _searchedQuery, so they vanish as the user edits the query
    // and reappear (without a new call) if they type it back.
    private IReadOnlyList<DomainInstrument>? _searchResults;
    private string _searchedQuery = string.Empty;

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
        PlaceholderText = "Filter watchlist, or type a symbol/name and press Enter to search...";
    }

    // Typing only re-lists — never calls the API. The online search is Enter-only.
    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (_quotes is null)
            return [];

        return string.IsNullOrEmpty(SearchText) ? BrowseItems() : SearchItems();
    }

    // Empty search box: the full watchlist — favorites first, then per-category sections.
    private IListItem[] BrowseItems()
    {
        var items = new List<IListItem>();

        var favorites = _quotes!.Where(q => FavoritesStore.Instance.IsFavorite(q.Symbol)).ToArray();
        foreach (var q in favorites)
            items.Add(BuildItem(q, "★ Favorites"));

        foreach (var category in new[] { AssetCategory.Stock, AssetCategory.Crypto, AssetCategory.Currency })
        {
            var inCategory = _quotes!.Where(q => q.Category == category && !FavoritesStore.Instance.IsFavorite(q.Symbol));
            foreach (var q in inCategory)
                items.Add(BuildItem(q, SectionLabel(category)));
        }

        items.Add(new ListItem(new RefreshCommand(this)) { Title = "Refresh 🔄" });
        return items.ToArray();
    }

    // Non-empty search box: the Enter-to-search action, then instant local matches, then the last
    // online search's results (if they belong to the current query).
    private IListItem[] SearchItems()
    {
        var items = new List<IListItem>
        {
            // First item → pressing Enter runs the one /search call (see RunSearchCommand).
            new ListItem(new RunSearchCommand(this))
            {
                Title = $"Search Finnhub for \"{SearchText}\"",
                Subtitle = "Press Enter to look up symbols online",
                Icon = new IconInfo(SearchGlyph),
            },
        };

        // Instant, no-API filter over the already-loaded watchlist.
        var localMatches = _quotes!.Where(q =>
            q.Symbol.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            q.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var q in localMatches)
            items.Add(BuildItem(q, "Watchlist"));

        // Online results, only while they still match what's typed.
        if (_searchResults is not null &&
            string.Equals(_searchedQuery, SearchText, StringComparison.OrdinalIgnoreCase))
        {
            // Hide results already in the watchlist — they show in the Watchlist section above.
            var inWatchlist = _quotes!.Select(q => q.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = _searchResults.Where(r => !inWatchlist.Contains(r.Symbol)).ToArray();

            if (fresh.Length == 0 && localMatches.Length == 0)
            {
                items.Add(new ListItem(new NoOpCommand())
                {
                    Title = $"No matches for \"{SearchText}\"",
                    Section = "Search results",
                });
            }

            foreach (var instrument in fresh)
                items.Add(BuildResultItem(instrument));
        }

        return items.ToArray();
    }

    // A priced watchlist row. Stars/unstars in place (the row is already priced, so no refetch).
    private ListItem BuildItem(UiQuote q, string section) =>
        new(new ToggleFavoriteCommand(new DomainInstrument(q.Symbol, q.Name, q.Category), () => RaiseItemsChanged(0)))
        {
            Title = $"{q.Symbol} · {q.Name}",
            Subtitle = $"{q.FormatPrice()}   {q.FormatChange()}",
            Section = section,
            MoreCommands =
            [
                new CommandContextItem(new CopyTextCommand(q.FormatPrice())) { Title = "Copy price" },
            ],
        };

    // A price-less online search result. Adding it pins the instrument and re-prices the watchlist
    // (RefreshQuotes) so it appears under ★ Favorites with a live price.
    private ListItem BuildResultItem(DomainInstrument instrument)
    {
        var pinned = FavoritesStore.Instance.IsFavorite(instrument.Symbol);
        return new ListItem(new ToggleFavoriteCommand(instrument, () => RefreshQuotes()))
        {
            Title = $"{instrument.Symbol} · {instrument.Name}",
            Subtitle = pinned ? "★ In watchlist" : "Press Enter to add to watchlist",
            Section = "Search results",
        };
    }

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
        _quotes = [.. (await _repository.GetQuotesAsync(Watchlist.Instruments())).Select(UiQuote.From)];
        IsLoading = false;
        RaiseItemsChanged(0);
    }

    // Runs the single online /search call for the current SearchText, then re-lists with results.
    internal void RunSearch()
    {
        var query = SearchText;
        if (string.IsNullOrWhiteSpace(query))
            return;

        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(() => SearchOnline(query));
    }

    private async Task SearchOnline(string query)
    {
        var results = await _repository.SearchAsync(query);
        _searchResults = results;
        _searchedQuery = query;
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

    private sealed partial class RunSearchCommand : InvokableCommand
    {
        private readonly MarketsPage _page;

        public RunSearchCommand(MarketsPage page)
        {
            _page = page;
            Name = "Search";
            Icon = new IconInfo(SearchGlyph);
        }

        public override ICommandResult Invoke()
        {
            _page.RunSearch();
            return CommandResult.KeepOpen();
        }
    }
}

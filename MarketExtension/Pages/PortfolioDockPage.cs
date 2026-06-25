using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Backs a second Command Palette Dock band (next to the favorites band): a single strip showing the
// portfolio's total value + today's P&L, rolled up into the user's PortfolioCurrency. Clicking it opens the
// full PortfolioPage. Returned from MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
//
// Unlike the favorites band (one button per instrument), this band renders ONE summary button — the same
// totals row the Portfolio screen pins on top — so the dock shows the bottom line at a glance.
//
// Live lifecycle mirrors FavoritesDockPage: the project's INotifyItemsChanged on-load refresh re-reads
// holdings + quotes every time the band becomes visible, it subscribes to PortfolioStore.Positions (so a
// quantity/membership change repaints at once) and to PollTicker (so prices refresh on the timer), and it
// disposes both when hidden so a hidden band does no work. Multi-currency: the band prices every holding,
// primes the native->preferred FX rates (CurrencyConverter / Frankfurter), and rolls them up via
// UiPortfolio — exactly like PortfolioPage, but it AWAITS the FX prime inside the async load so the
// converted total is ready in one paint (no progressive native-only first frame needed on a one-row band).
internal sealed partial class PortfolioDockPage : ListPage, INotifyItemsChanged
{
    private const string PortfolioGlyph = ""; // Segoe MDL2 Bank glyph U+E825 (matches PortfolioPage's summary row)

    private readonly MarketRepository _repository;
    private UiQuote[]? _quotes;       // last priced holdings, for the keep-last-good poll merge
    private UiPortfolio? _portfolio;  // the rolled-up total for rendering; null until first load lands

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    private IDisposable? _subscription;
    private IDisposable? _pollSubscription;
    private IDisposable? _demoModeSubscription;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the holdings flow while the band is visible: its replay paints the band the moment it
            // opens, and any later add/edit/remove from the detail page refreshes it at once. Positions uses
            // reference equality, so even a quantity-only edit re-emits and re-rolls the total.
            _subscription = PortfolioStore.Instance.Positions.Subscribe(_ => RefreshPortfolio());
            // Live polling: each tick silently re-prices holdings in place (no spinner). The ticker is a pure
            // event stream (no replay), so becoming visible doesn't double-fetch — the positions subscription
            // above already paints.
            _pollSubscription = PollTicker.Subscribe(PollRefresh);
            // Demo mode flips the data source — re-roll the total from the new one at once if the band is
            // open. A hidden band already re-fetches on its next open (the positions-flow replay calls
            // RefreshPortfolio), so this only needs to cover a currently-visible/pinned band. replay:false —
            // opening already paints via the positions subscription above.
            _demoModeSubscription = MarketSettingsManager.Instance.DemoModeChanged
                .Subscribe(_ => RefreshPortfolio(), replayOnSubscribe: false);
            Log.Info("Poll", $"PortfolioDock: started polling [{string.Join(", ", PortfolioStore.Instance.Positions.Value.Select(p => p.Instrument.Symbol))}]");
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
            Log.Info("Poll", "PortfolioDock: stopped polling");
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public PortfolioDockPage(MarketRepository repository)
    {
        _repository = repository;
        Id = "com.costafotiadis.market.dock.portfolio"; // dock bands require a non-empty command Id
        Title = "Markets Portfolio";
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
    }

    public override IListItem[] GetItems()
    {
        if (_quotes is null)
            return []; // not loaded yet

        if (_portfolio is null || !_portfolio.HasHoldings)
        {
            return [new ListItem(new NoOpCommand { Id = "com.costafotiadis.market.dock.portfolio.empty" })
            {
                Title = "No holdings yet",
                Subtitle = "Add holdings from an instrument's detail page (Add to Portfolio)",
            }];
        }

        // The one summary button: total value as the title, today's P&L (and any "not converted" note) as the
        // subtitle. Clicking opens the full Portfolio screen for the breakdown.
        return
        [
            new ListItem(new PortfolioPage(_repository))
            {
                Title = $"Portfolio {_portfolio.FormatTotalValue()}",
                Subtitle = _portfolio.FormatTotalChange() + _portfolio.FormatTotalReturnNote() + _portfolio.FormatUnconvertedNote(),
                Icon = new IconInfo(PortfolioGlyph),
            },
        ];
    }

    internal void RefreshPortfolio()
    {
        _quotes = null;
        _portfolio = null;
        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(() => LoadPortfolio(silent: false));
    }

    // Live-poll refresh: re-price holdings WITHOUT clearing _quotes/_portfolio or showing a spinner, so the
    // current total stays on the band until LoadPortfolio swaps the new one in (no flicker). Skips work
    // before the first paint — the positions subscription already has a load in flight then.
    internal void PollRefresh()
    {
        if (_quotes is null)
            return;
        Log.Info("Poll", $"PortfolioDock: re-pricing holdings [{string.Join(", ", PortfolioStore.Instance.Positions.Value.Select(p => p.Instrument.Symbol))}]");
        Task.Run(() => LoadPortfolio(silent: true));
    }

    // Price the current holdings, convert into the preferred currency, and roll up the total. `silent` = a
    // background poll (no spinner): in that mode, don't let a transient bad result (e.g. a 429 mapped to an
    // invalid quote) overwrite a price that was fine a moment ago, which would otherwise drop that holding
    // from the total and make it jump. Same keep-last-good guard as FavoritesDockPage / PricedListPage.
    private async Task LoadPortfolio(bool silent)
    {
        var positions = PortfolioStore.Instance.Positions.Value; // snapshot the holdings to price
        if (positions.Count == 0)
        {
            _quotes = [];        // loaded-but-empty (distinct from null = not-loaded), so GetItems shows the empty row
            _portfolio = null;
            IsLoading = false;
            RaiseItemsChanged(0);
            return;
        }

        var instruments = positions.Select(p => p.Instrument).ToList();
        var prior = _quotes; // on-screen prices, for the keep-last-good merge
        IEnumerable<UiQuote> fetched =
            (await _repository.GetQuotesAsync(instruments)).Select(UiQuote.From);

        if (silent && prior is not null)
        {
            var lastGood = prior
                .Where(q => q.IsValid)
                .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            fetched = fetched.Select(q =>
                !q.IsValid && lastGood.TryGetValue(q.Symbol, out var good) ? good : q);
        }

        var quotes = fetched.ToArray();
        _quotes = quotes;

        // Prime native->preferred FX rates before rolling up, so the converted total is ready this paint.
        var preferred = MarketSettingsManager.Instance.PortfolioCurrency;
        var natives = quotes
            .Where(q => q.IsValid)
            .Select(q => q.Source.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (natives.Length > 0)
            await CurrencyConverter.Instance.PrimeAsync(preferred, natives);

        _portfolio = BuildPortfolio(positions, quotes, preferred);
        IsLoading = false;
        RaiseItemsChanged(0);
    }

    // Zip each holding's quantity with its priced quote and the (cached) native->preferred rate, then roll
    // the set up into the totals — the same composition PortfolioPage.LeadingRows does, just eager here.
    private static UiPortfolio BuildPortfolio(IReadOnlyList<DomainPosition> positions, UiQuote[] quotes, string preferred)
    {
        var quoteBySymbol = quotes
            .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Source, StringComparer.OrdinalIgnoreCase);

        var uiPositions = new List<UiPosition>();
        foreach (var p in positions)
            if (quoteBySymbol.TryGetValue(p.Instrument.Symbol, out var quote))
            {
                var rate = CurrencyConverter.Instance.TryGetRate(quote.Currency, preferred);
                uiPositions.Add(UiPosition.From(quote, p.Quantity, preferred, rate, p.CostBasis));
            }

        return UiPortfolio.From(uiPositions, preferred);
    }
}

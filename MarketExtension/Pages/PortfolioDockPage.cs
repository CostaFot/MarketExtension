using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Backs a second Command Palette Dock band (next to the favorites band): a single strip showing the
// portfolio's total value + today's P&L, rolled up into the user's PortfolioCurrency. Clicking it opens the
// full PortfolioPage. Returned from MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
//
// Unlike the favorites band (one button per instrument), this band renders ONE summary button — the same
// totals row the Portfolio screen pins on top — so the dock shows the bottom line at a glance.
//
// A PURE OBSERVER of the shared quote cache, like FavoritesDockPage: while visible it subscribes to the
// repository's cache-backed quote stream for the holdings (MarketRepository.ObserveQuotes over
// PortfolioStore.Instruments) and renders whatever it emits. It does NOT fetch, poll, or handle demo-mode
// flips itself — the repository owns all of that (its single poll loop refreshes the observed set on a timer,
// and it refills the cache on a source flip), and the cache owns keep-last-good — so this band can never
// drift out of sync with the Portfolio screen. Because PortfolioStore.Instruments re-emits on EVERY mutation
// (reference equality), the membership-aware ObserveQuotes Switch also re-fires on a quantity-only edit, so
// the handler re-reads PortfolioStore.Positions and re-rolls the total.
//
// The one wrinkle over the favorites band is async PROJECTION: rolling up needs native->preferred FX rates,
// an async fetch, so the observe handler AWAITS CurrencyConverter.PrimeAsync before building the total — one
// paint, no progressive native-only frame (a one-row band has nothing to gain from that). This is safe
// because ObserveQuotes delivers via ObserveOn (see MarketRepository): the handler runs on a pool thread with
// NO Rx gate lock held, so RaiseItemsChanged's COM call into the host can't re-enter under a producer-side
// lock (the deadlock the favorites band hit before the ObserveOn fix).
internal sealed partial class PortfolioDockPage : ListPage, INotifyItemsChanged
{
    private const string PortfolioGlyph = "\uE825"; // Segoe MDL2 Bank glyph U+E825 (matches PortfolioPage's summary row)

    private readonly MarketRepository _repository;
    private UiPortfolio? _portfolio;  // the rolled-up total for rendering; null until the first roll-up lands

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    // Subscriptions in a list (not a single field) so a double-`add` without an intervening `remove` can't
    // orphan a subscription — which would also leave its symbols pinned to the repository's poll loop. Same
    // reasoning and pattern as FavoritesDockPage / PricedListPage.
    private readonly List<IDisposable> _subscriptions = [];

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the cache-backed quote stream for the holdings while the band is visible. The
            // membership-aware overload replays the holdings' cached quotes on subscribe (painting at once,
            // fetching only symbols not already cached), re-projects on any add/remove/quantity edit
            // (Instruments re-emits on every mutation), and re-emits whenever a member quote changes in the
            // cache (so the repository's poll/demo refresh repaints the band). Delivery is off-thread
            // (ObserveOn in the repository). Disposed in `remove` — a hidden band does no work and unregisters
            // its symbols so the repository stops polling them. No PollTicker / DemoModeChanged subscription
            // and no keep-last-good merge here anymore: the repository polls the observed set and refills on a
            // source flip, and the cache holds the last good price through a transient bad fetch.
            _subscriptions.Add(_repository.ObserveQuotes(PortfolioStore.Instance.Instruments)
                .Subscribe(quotes => _ = OnQuotesChangedAsync(quotes)));
            Log.Info("Dock", $"observing portfolio [{string.Join(", ", PortfolioStore.Instance.Positions.Value.Select(p => p.Instrument.Symbol))}]");
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            Log.Info("Dock", "stopped observing portfolio");
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public PortfolioDockPage(MarketRepository repository)
    {
        _repository = repository;
        Id = "com.costafotiadis.market.dock.portfolio"; // dock bands require a non-empty command Id
        Title = Resources.Command_MarketsPortfolio;
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
    }

    public override IListItem[] GetItems()
    {
        // No holdings at all → the empty-state row. Read membership (not _portfolio) so this shows only when
        // the portfolio is genuinely empty, never during the pre-first-emission window.
        if (PortfolioStore.Instance.Positions.Value.Count == 0)
        {
            return [new ListItem(new NoOpCommand { Id = "com.costafotiadis.market.dock.portfolio.empty" })
            {
                Title = Resources.Portfolio_Empty_Title,
                Subtitle = Resources.Portfolio_Empty_Subtitle,
            }];
        }

        // Holdings exist but the roll-up isn't ready yet (prices still filling the cache) → let the spinner show.
        if (_portfolio is null)
            return [];

        // The one summary button: total value as the title, today's P&L (and any total-return / "not
        // converted" notes) as the subtitle. Clicking opens the full Portfolio screen for the breakdown.
        return
        [
            new ListItem(new PortfolioPage(_repository))
            {
                Title = Strings.Format(Resources.Portfolio_TotalsRow_Title, _portfolio.FormatTotalValue()),
                Subtitle = _portfolio.FormatTotalChange() + _portfolio.FormatTotalReturnNote() + _portfolio.FormatUnconvertedNote(),
                Icon = new IconInfo(PortfolioGlyph),
            },
        ];
    }

    // A new cache emission for the holdings: roll up the total and repaint. Runs on a pool thread (ObserveOn)
    // with no Rx gate lock held, so RaiseItemsChanged's host call is safe, and awaiting the FX prime here is
    // what makes the converted total land in a single paint. Launched fire-and-forget from the synchronous
    // Subscribe lambda (`_ = ...`), with its own try/catch so no exception escapes onto the pool thread.
    private async Task OnQuotesChangedAsync(IReadOnlyList<DomainQuote> quotes)
    {
        try
        {
            var positions = PortfolioStore.Instance.Positions.Value;

            // Holdings exist but their prices haven't landed in the cache yet → keep the spinner, don't roll up.
            if (positions.Count > 0 && quotes.Count == 0)
            {
                Log.Info("Dock", $"portfolio: {positions.Count} holding(s) but no cached prices yet — spinner");
                _portfolio = null;
                IsLoading = true;
                RaiseItemsChanged(0);
                return;
            }

            // Prime native->preferred FX rates BEFORE rolling up, so the converted total is ready this paint.
            // PrimeAsync skips currencies it already has fresh, so a steady portfolio does no extra network.
            var preferred = MarketSettingsManager.Instance.PortfolioCurrency;
            var natives = quotes
                .Where(q => q.IsValid)
                .Select(q => q.Currency)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (natives.Length > 0)
                await CurrencyConverter.Instance.PrimeAsync(preferred, natives);

            _portfolio = BuildPortfolio(positions, quotes, preferred);
            Log.Info("Dock",
                $"portfolio rolled up: {_portfolio.FormatTotalValue()} {_portfolio.FormatTotalChange()} across {positions.Count} holding(s)");
            IsLoading = false;
            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("Dock", "portfolio roll-up failed", ex);
        }
    }

    // Zip each holding's quantity with its priced quote and the (cached) native->preferred rate, then roll
    // the set up into the totals — the same composition PortfolioPage.LeadingRows does, just eager here.
    private static UiPortfolio BuildPortfolio(
        IReadOnlyList<DomainPosition> positions, IReadOnlyList<DomainQuote> quotes, string preferred)
    {
        var quoteBySymbol = quotes
            .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

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

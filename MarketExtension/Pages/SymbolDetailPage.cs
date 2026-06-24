using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// The shared per-symbol detail screen, reached by pressing Enter on any instrument row (Search,
// Watchlist, Favorites) and by clicking any dock button. The body is a live price chart with
// Robinhood-style range tabs (1D / 1W / 1M / 1Y / 5Y): an adaptive-card FormContent whose Action.Submit
// tab buttons call back into SymbolChartForm.SubmitForm to switch range, refetch candles, and repaint.
//
// List management lives on the page's command bar: the add/remove-watchlist action is the primary
// command (Enter) and add/remove-favorite the secondary (Ctrl+Enter), labelled for current state.
//
// ⚠️ KNOWN ISSUE (unsolved — see the "Symbol detail + live chart" section of CLAUDE.md): with a single
// FormContent the host marks it OnlyControlOnPage and auto-focuses the card's first Action.Submit (the
// 1D tab), so Enter activates that tab instead of the primary command — and with focus trapped in the
// card, Ctrl+Enter doesn't reach the secondary either. Both attempted fixes were rejected (a 2nd content
// item to drop OnlyControlOnPage did NOT keep focus in the search box in practice; leading the card with
// membership buttons was too hacky). Left as-is for now.
//
// Reactive like the rest of the app (see Helpers/StateFlow.cs + the "prefer observable data flow"
// convention): it subscribes to WatchlistStore's two membership flows while visible — the
// INotifyItemsChanged add/remove lifecycle, the same seam the list pages use — and rebuilds its Commands
// on every change. That same "page became visible" hook starts the chart's first load, so candles are
// fetched only when the page is actually opened (not when a list builds a row per item), and subscribes
// the chart to the shared PollTicker so the visible range live-refreshes on each tick while the page is up.
internal sealed partial class SymbolDetailPage : ContentPage, INotifyItemsChanged
{
    private readonly DomainInstrument _instrument;
    private readonly SymbolChartForm _chartForm;
    private readonly List<IDisposable> _subscriptions = [];

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Replay-on-subscribe paints the initial command bar; later pushes flip it in place.
            _subscriptions.Add(WatchlistStore.Instance.Watchlist.Subscribe(_ => RefreshCommands()));
            _subscriptions.Add(WatchlistStore.Instance.Favorites.Subscribe(_ => RefreshCommands()));
            // This accessor fires only when the page is actually shown — the right moment to kick the
            // first candle fetch (once). Building a row's SymbolDetailPage doesn't subscribe, so list
            // rows never trigger a fetch.
            _chartForm.Start();
            // Live refresh: re-fetch the visible range on each poll tick while the page is shown, sharing
            // the one PollTicker (and its OnActive/OnInactive refcount) with the priced list pages + dock.
            // replayOnSubscribe:false so opening the page doesn't double-fetch — Start() already did the
            // first load. Disposed in `remove` with the rest, so a hidden page neither polls nor leaks.
            _subscriptions.Add(PollTicker.Instance.Subscribe(_ => _chartForm.PollRefresh(), replayOnSubscribe: false));
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public SymbolDetailPage(DomainInstrument instrument, MarketRepository repository)
    {
        _instrument = instrument;
        _chartForm = new SymbolChartForm(instrument, repository);
        // Stable, unique per symbol — also lets this page stand in as a Dock band item's command,
        // where a non-empty Id is required.
        Id = $"com.costafotiadis.market.detail.{instrument.Symbol}";
        Icon = AssetIconResolver.Resolve(instrument);
        Title = $"{instrument.Symbol} · {instrument.Name}";
        Name = "View details";
        Commands = BuildCommands(); // initial bar; refreshed again on subscribe and on every change
    }

    public override IContent[] GetContent() => [_chartForm];

    // Membership changed → re-read the command bar (the chart is independent of membership). Setting
    // Commands raises the property change the host watches, so the add/remove buttons flip in place.
    private void RefreshCommands() => Commands = BuildCommands();

    // The two list-management actions, labelled add/remove for the instrument's current state. The first
    // is the page's primary command (Enter), the second the secondary (Ctrl+Enter).
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

    // The adaptive-card chart body. Holds the current range + a per-range cache, fetches candles via
    // the repository, and repaints by updating DataJson (FormContent : BaseObservable, so the host
    // re-renders in place — the same mechanism Perf Monitor's widget cards use). PollRefresh re-fetches
    // the visible range on each PollTicker tick so an open chart stays live, not just the list pages.
    private sealed partial class SymbolChartForm : FormContent
    {
        private readonly DomainInstrument _instrument;
        private readonly MarketRepository _repository;
        private readonly Dictionary<ChartRange, DomainCandleSeries> _cache = [];
        private readonly Lock _gate = new();
        private ChartRange _range = ChartRange.OneDay;
        private int _generation; // bumped per fetch so a superseded tab tap's late result is dropped
        private bool _started;
        private DomainCandleSeries? _displaySeries; // what's currently painted (null = a "Loading…" card)

        public SymbolChartForm(DomainInstrument instrument, MarketRepository repository)
        {
            _instrument = instrument;
            _repository = repository;
            TemplateJson = Template;
            DataJson = BuildData(series: null, _range); // a "Loading…" card until the first fetch lands
        }

        // Called from the page's visible hook — fetch the default range exactly once.
        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                    return;
                _started = true;
            }

            Load(_range);
        }

        // A range tab was tapped. The selected range rides in the Action.Submit `data` ({ "range": "1W" });
        // fall back to the form inputs just in case the host merges them. Membership lives on the page's
        // command bar (Enter / Ctrl+Enter), not the card — so the card only switches range.
        public override ICommandResult SubmitForm(string inputs, string data)
        {
            var range = ParseRange(data) ?? ParseRange(inputs) ?? _range;
            Load(range);
            return CommandResult.KeepOpen();
        }

        private void Load(ChartRange range)
        {
            _range = range;

            DomainCandleSeries? cached;
            bool hasChart;
            lock (_gate)
            {
                _cache.TryGetValue(range, out cached);
                hasChart = _displaySeries?.HasData == true;
            }

            if (cached is not null)
            {
                Render(cached);
                return;
            }

            int generation;
            lock (_gate)
                generation = ++_generation;

            // Only blank to a "Loading…" card when no chart is on screen yet (the very first load). On
            // later range switches we leave the prior chart visible and swap it in place once the fetch
            // lands — each DataJson write rebuilds the whole card, so a Loading→result pair flashes.
            if (!hasChart)
                DataJson = BuildData(series: null, range);

            Task.Run(async () =>
            {
                var series = await _repository.GetCandlesAsync(_instrument, range);
                lock (_gate)
                {
                    if (generation != _generation)
                        return; // a newer tab tap superseded this one — drop the stale result
                    if (series.HasData)
                        _cache[range] = series;
                }

                Render(series);
            });
        }

        private void Render(DomainCandleSeries series)
        {
            // Remember what's on screen — drives the flicker guard: a real chart present means a later
            // range switch keeps it up instead of flashing "Loading" (see Load).
            lock (_gate)
                _displaySeries = series;

            DataJson = BuildData(series, series.Range);
        }

        // Live-poll repaint, driven by PollTicker while the page is visible. Re-fetches the CURRENTLY
        // shown range — bypassing the per-range cache so the on-screen range actually refreshes — then
        // swaps the result in place and refreshes that cache entry. Silent by design: a chart is already
        // up, so it never writes the "Loading…" card (no flicker), unlike a tab tap's first paint. Reuses
        // the generation guard so a concurrent tab tap still wins, and keeps the last good chart when a
        // poll returns empty (a transient 403/429/no_data won't blank a chart that was fine) — the chart's
        // analog of PricedListPage's keep-last-good guard.
        internal void PollRefresh()
        {
            ChartRange range;
            int generation;
            bool hasChart;
            lock (_gate)
            {
                if (!_started)
                    return; // first load hasn't run yet — nothing to refresh
                range = _range;
                hasChart = _displaySeries?.HasData == true;
                generation = ++_generation;
            }

            Task.Run(async () =>
            {
                var series = await _repository.GetCandlesAsync(_instrument, range);
                lock (_gate)
                {
                    if (generation != _generation)
                        return; // a tab tap or newer poll superseded this fetch — drop it
                    if (!series.HasData && hasChart)
                        return; // keep-last-good: don't blank a good chart on a transient empty poll
                    if (series.HasData)
                        _cache[range] = series;
                }

                Render(series);
            });
        }

        private static ChartRange? ParseRange(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("range", out var range) &&
                    range.ValueKind == JsonValueKind.String)
                    return ChartRangeExtensions.FromLabel(range.GetString());
            }
            catch (JsonException)
            {
                // Not our payload — ignore and let the caller fall back.
            }

            return null;
        }

        // Build the card payload for the current display state. A null series paints the "Loading…"
        // placeholder for `range`; otherwise the chart (or the premium-gated "no data" message).
        private string BuildData(DomainCandleSeries? series, ChartRange range)
        {
            var data = new JsonObject
            {
                ["symbol"] = _instrument.Symbol,
                ["name"] = _instrument.Name,
            };

            if (series is null)
            {
                data["price"] = "—";
                data["change"] = string.Empty;
                data["changeColor"] = "Default";
                data["chartUrl"] = string.Empty;
                data["hasChart"] = false;
                data["showStatus"] = true;
                data["statusText"] = $"Loading {range.Label()} chart…";
                return data.ToJsonString();
            }

            var ui = UiCandleSeries.From(series);
            var hasChart = ui.HasData;
            data["price"] = ui.FormatPrice();
            data["change"] = ui.FormatRangeChange();
            data["changeColor"] = !hasChart ? "Default" : ui.IsUp ? "Good" : "Attention";
            data["chartUrl"] = ui.ChartImageUrl();
            data["hasChart"] = hasChart;
            data["showStatus"] = !hasChart;
            data["statusText"] = hasChart
                ? string.Empty
                : "No chart data available for this range.";
            return data.ToJsonString();
        }

        // Static card structure; values bind from DataJson. The five Action.Submit buttons each carry
        // their range label, which SubmitForm reads back.
        private const string Template = """
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            { "type": "TextBlock", "text": "${symbol}", "size": "ExtraLarge", "weight": "Bolder", "wrap": true },
            { "type": "TextBlock", "text": "${name}", "isSubtle": true, "spacing": "None", "wrap": true },
            { "type": "TextBlock", "text": "${price}", "size": "Large", "weight": "Bolder", "spacing": "Medium" },
            { "type": "TextBlock", "text": "${change}", "color": "${changeColor}", "weight": "Bolder", "spacing": "None", "wrap": true },
            { "type": "Image", "url": "${chartUrl}", "size": "Stretch", "$when": "${hasChart}" },
            { "type": "TextBlock", "text": "${statusText}", "isSubtle": true, "wrap": true, "spacing": "Medium", "$when": "${showStatus}" },
            {
              "type": "ActionSet",
              "spacing": "Medium",
              "actions": [
                { "type": "Action.Submit", "title": "1D", "data": { "range": "1D" } },
                { "type": "Action.Submit", "title": "1W", "data": { "range": "1W" } },
                { "type": "Action.Submit", "title": "1M", "data": { "range": "1M" } },
                { "type": "Action.Submit", "title": "1Y", "data": { "range": "1Y" } },
                { "type": "Action.Submit", "title": "5Y", "data": { "range": "5Y" } }
              ]
            },
            { "type": "TextBlock", "text": "Logos provided by Elbstream", "isSubtle": true, "wrap": true, "spacing": "Medium" }
          ]
        }
        """;
    }
}

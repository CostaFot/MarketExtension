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
// List management still lives here too: the page's command bar carries the add/remove-watchlist (Enter)
// and add/remove-favorite (Ctrl+Enter) actions, labelled for the instrument's current state.
//
// Reactive like the rest of the app (see Helpers/StateFlow.cs + the "prefer observable data flow"
// convention): it subscribes to WatchlistStore's two membership flows while visible — the
// INotifyItemsChanged add/remove lifecycle, the same seam the list pages use — and rebuilds its
// Commands on every change. That same "page became visible" hook starts the chart's first load, so
// candles are fetched only when the page is actually opened (not when a list builds a row per item).
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
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public SymbolDetailPage(DomainInstrument instrument, MarketRepository repository)
    {
        _instrument = instrument;
        _chartForm = new SymbolChartForm(instrument, repository);
        // Stable, unique per symbol — also lets this page stand in as a Dock band item's command,
        // where a non-empty Id is required.
        Id = $"com.costafotiadis.market.detail.{instrument.Symbol}";
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = $"{instrument.Symbol} · {instrument.Name}";
        Name = "View details";
        Commands = BuildCommands(); // initial bar; refreshed again on subscribe and on every change
    }

    public override IContent[] GetContent() => [_chartForm];

    // Membership changed → re-read the command bar (the chart is independent of membership). Setting
    // Commands raises the property change the host watches, so the add/remove buttons flip in place.
    private void RefreshCommands() => Commands = BuildCommands();

    // The two list-management actions, labelled add/remove for the instrument's current state. The
    // first is the page's primary command (Enter), the second the secondary (Ctrl+Enter).
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
    // re-renders in place — the same mechanism Perf Monitor's widget cards use).
    private sealed partial class SymbolChartForm : FormContent
    {
        private readonly DomainInstrument _instrument;
        private readonly MarketRepository _repository;
        private readonly Dictionary<ChartRange, DomainCandleSeries> _cache = [];
        private readonly Lock _gate = new();
        private ChartRange _range = ChartRange.OneDay;
        private int _generation; // bumped per fetch so a superseded tab tap's late result is dropped
        private bool _started;

        public SymbolChartForm(DomainInstrument instrument, MarketRepository repository)
        {
            _instrument = instrument;
            _repository = repository;
            TemplateJson = Template;
            DataJson = LoadingData(_range); // placeholder until the first fetch lands
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
        // fall back to the form inputs just in case the host merges them.
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
            lock (_gate)
                _cache.TryGetValue(range, out cached);

            if (cached is not null)
            {
                Render(cached);
                return;
            }

            int generation;
            lock (_gate)
                generation = ++_generation;

            DataJson = LoadingData(range); // show a loading state for the newly selected range
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

        private void Render(DomainCandleSeries series) => DataJson = BuildData(UiCandleSeries.From(series));

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

        private string LoadingData(ChartRange range) => new JsonObject
        {
            ["symbol"] = _instrument.Symbol,
            ["name"] = _instrument.Name,
            ["price"] = "—",
            ["change"] = string.Empty,
            ["changeColor"] = "Default",
            ["chartUrl"] = string.Empty,
            ["hasChart"] = false,
            ["showStatus"] = true,
            ["statusText"] = $"Loading {range.Label()} chart…",
        }.ToJsonString();

        private string BuildData(UiCandleSeries ui)
        {
            var hasChart = ui.HasData;
            return new JsonObject
            {
                ["symbol"] = _instrument.Symbol,
                ["name"] = _instrument.Name,
                ["price"] = ui.FormatPrice(),
                ["change"] = ui.FormatRangeChange(),
                ["changeColor"] = !hasChart ? "Default" : ui.IsUp ? "Good" : "Attention",
                ["chartUrl"] = ui.ChartImageUrl(),
                ["hasChart"] = hasChart,
                ["showStatus"] = !hasChart,
                ["statusText"] = hasChart
                    ? string.Empty
                    : "No chart data — historical candles require a paid Finnhub plan.",
            }.ToJsonString();
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
            }
          ]
        }
        """;
    }
}

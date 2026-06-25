using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// JSON-backed set of the user's portfolio HOLDINGS (a quantity per instrument), persisted under the
// CmdPal settings folder so it survives reloads. Modeled on WatchlistStore, but it tracks a quantity
// (and an optional, currently-unused cost basis) rather than membership flags, and it does NOT seed any
// defaults — a fresh install starts with an empty portfolio.
//
// Two observable StateFlows drive the UI:
//   * Positions  — the holdings (instrument + quantity), for rendering rows and the totals summary, and
//                  for the detail page's command bar (Add vs Edit/Remove).
//   * Instruments — just the symbols to price, which the Portfolio screen (a PricedListPage) consumes.
//
// Both use DEFAULT (reference) equality — NOT InstrumentListComparer — so EVERY mutation re-emits,
// including a quantity-only edit that leaves the symbol set unchanged. That's what makes an edited
// quantity repaint: the PricedListPage's reconcile finds nothing missing (no fetch) and just re-renders,
// re-reading the new quantity. (Watchlist/Favorites dedup by symbol because only membership matters
// there; here the quantity matters too, so we want every change through.)
internal sealed class PortfolioStore
{
    public static readonly PortfolioStore Instance = new();

    private readonly Lock _lock = new();
    // Keyed by normalized symbol (reusing WatchlistStore.Normalize so a holding and its watchlist/quote
    // cache entries line up); value is the full holding.
    private Dictionary<string, DomainPosition> _positions = [];

    private readonly MutableStateFlow<IReadOnlyList<DomainPosition>> _positionsFlow = new([]);
    private readonly MutableStateFlow<IReadOnlyList<DomainInstrument>> _instrumentsFlow = new([]);

    // The holdings, in insertion order. Subscribe for live updates; read .Value for a snapshot.
    public StateFlow<IReadOnlyList<DomainPosition>> Positions => _positionsFlow;

    // The instruments to price (holding identities only), for the Portfolio PricedListPage.
    public StateFlow<IReadOnlyList<DomainInstrument>> Instruments => _instrumentsFlow;

    private static string JsonPath => Path.Combine(SettingsDirectory, "market_portfolio.json");

    private static string SettingsDirectory
    {
        get
        {
            var directory = Utilities.BaseSettingsPath("Microsoft.CmdPal");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private PortfolioStore()
    {
        Load();
        PublishState(); // seed the flows with the loaded holdings, before any subscriber exists
    }

    public bool Contains(string symbol)
    {
        lock (_lock) return _positions.ContainsKey(WatchlistStore.Normalize(symbol));
    }

    public bool TryGetQuantity(string symbol, out decimal quantity)
    {
        lock (_lock)
        {
            if (_positions.TryGetValue(WatchlistStore.Normalize(symbol), out var p))
            {
                quantity = p.Quantity;
                return true;
            }
        }

        quantity = 0m;
        return false;
    }

    // Add or update a holding (a SetPosition with the same symbol overwrites its quantity). Preserves any
    // existing cost basis unless a new one is given, so a quantity-only edit doesn't wipe it.
    public void SetPosition(DomainInstrument instrument, decimal quantity, decimal? costBasis = null)
    {
        var key = WatchlistStore.Normalize(instrument.Symbol);
        lock (_lock)
        {
            var basis = costBasis ?? (_positions.TryGetValue(key, out var existing) ? existing.CostBasis : null);
            _positions[key] = new DomainPosition(instrument, quantity, basis);
        }

        Save();
        PublishState();
    }

    public void Remove(string symbol)
    {
        lock (_lock)
            _positions.Remove(WatchlistStore.Normalize(symbol));

        Save();
        PublishState();
    }

    // Recompute both flows and push them. Each call produces fresh list instances, so with default
    // equality every mutation emits (see the class comment) — quantity edits included.
    private void PublishState()
    {
        IReadOnlyList<DomainPosition> positions;
        IReadOnlyList<DomainInstrument> instruments;
        lock (_lock)
        {
            positions = [.. _positions.Values];
            instruments = [.. _positions.Values.Select(p => p.Instrument)];
        }

        _positionsFlow.Update(positions);
        _instrumentsFlow.Update(instruments);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(JsonPath))
            {
                var items = JsonSerializer.Deserialize(File.ReadAllText(JsonPath), PortfolioJsonContext.Default.ListPortfolioItem);
                if (items is not null)
                {
                    _positions = items.ToDictionary(
                        i => WatchlistStore.Normalize(i.Symbol),
                        i => new DomainPosition(new DomainInstrument(i.Symbol, i.Name, i.Category), i.Quantity, i.CostBasis));
                    return;
                }
            }

            // No file yet → empty portfolio. Unlike the watchlist, there's nothing to seed.
            _positions = [];
        }
        catch
        {
            _positions = [];
        }
    }

    private void Save()
    {
        try
        {
            List<PortfolioItem> snapshot;
            lock (_lock)
                snapshot = [.. _positions.Values.Select(p =>
                    new PortfolioItem(p.Instrument.Symbol, p.Instrument.Name, p.Instrument.Category, p.Quantity, p.CostBasis))];
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(snapshot, PortfolioJsonContext.Default.ListPortfolioItem));
        }
        catch
        {
            // best-effort
        }
    }
}

// Persistence DTO for one holding. Source-gen (de)serialized, matching the WatchlistStore convention.
internal sealed record PortfolioItem(string Symbol, string Name, AssetCategory Category, decimal Quantity, decimal? CostBasis);

// ⚠️ Keep ALL [JsonSerializable] on this single declaration — splitting them across partials silently
// breaks the JSON source generator (see CLAUDE.md AOT/trim gotcha).
[JsonSourceGenerationOptions(UseStringEnumConverter = true)] // category as readable, reorder-safe text
[JsonSerializable(typeof(List<PortfolioItem>))]
internal sealed partial class PortfolioJsonContext : JsonSerializerContext;

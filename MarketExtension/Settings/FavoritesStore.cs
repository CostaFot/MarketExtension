using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// JSON-backed set of pinned instruments, persisted under the CmdPal settings folder so the
// user's watchlist survives reloads. Adapted from reference/settings/FavoritesStore.cs.
//
// Stores full identity (symbol + name + category), not just the symbol, so an instrument added via
// search — which isn't in InstrumentCatalog — can still be re-priced on later loads. Composed with
// the catalog into the live watchlist by Watchlist.Instruments().
internal sealed class FavoritesStore
{
    public static readonly FavoritesStore Instance = new();

    private readonly Lock _lock = new();
    // Keyed by normalized symbol; value carries the identity needed to re-fetch a quote.
    private Dictionary<string, DomainInstrument> _favorites = [];

    private static string JsonPath
    {
        get
        {
            var directory = Utilities.BaseSettingsPath("Microsoft.CmdPal");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "market_favorites.json");
        }
    }

    private FavoritesStore() => Load();

    public bool IsFavorite(string symbol)
    {
        lock (_lock) return _favorites.ContainsKey(Normalize(symbol));
    }

    // The pinned instruments, in insertion order, as provider-agnostic identities.
    public IReadOnlyList<DomainInstrument> Pinned
    {
        get { lock (_lock) return [.. _favorites.Values]; }
    }

    // Pin if absent, unpin if present. Keyed on symbol; the full identity is stored on add so the
    // instrument can be re-priced later.
    public void Toggle(DomainInstrument instrument)
    {
        var key = Normalize(instrument.Symbol);
        lock (_lock)
        {
            if (!_favorites.Remove(key))
                _favorites[key] = instrument;
        }

        Save();
    }

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(JsonPath))
                return;

            var json = File.ReadAllText(JsonPath);

            // Current format: array of PinnedItem objects.
            var items = JsonSerializer.Deserialize(json, FavoritesJsonContext.Default.ListPinnedItem);
            if (items is { Count: > 0 })
            {
                _favorites = items.ToDictionary(
                    i => Normalize(i.Symbol),
                    i => new DomainInstrument(i.Symbol, i.Name, i.Category));
                return;
            }

            // Legacy format: array of bare symbol strings. Recover name/category from the catalog
            // where possible (so e.g. crypto favorites keep their category), defaulting to Stock.
            var symbols = JsonSerializer.Deserialize(json, FavoritesJsonContext.Default.ListString);
            if (symbols is { Count: > 0 })
                _favorites = symbols.ToDictionary(Normalize, ToInstrument);
        }
        catch
        {
            _favorites = [];
        }
    }

    private static DomainInstrument ToInstrument(string symbol)
    {
        var key = Normalize(symbol);
        var known = InstrumentCatalog.All.FirstOrDefault(i => Normalize(i.Symbol) == key);
        return known ?? new DomainInstrument(symbol, symbol, AssetCategory.Stock);
    }

    private void Save()
    {
        try
        {
            List<PinnedItem> snapshot;
            lock (_lock)
                snapshot = [.. _favorites.Values.Select(i => new PinnedItem(i.Symbol, i.Name, i.Category))];
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(snapshot, FavoritesJsonContext.Default.ListPinnedItem));
        }
        catch
        {
            // best-effort
        }
    }
}

// Persistence DTO for a pinned instrument. Source-gen (de)serialized so the trimmed Release build
// stays clean (reflection JSON here was the IL2026/IL3050 debt flagged in CLAUDE.md).
internal sealed record PinnedItem(string Symbol, string Name, AssetCategory Category);

[JsonSourceGenerationOptions(UseStringEnumConverter = true)] // category as readable, reorder-safe text
[JsonSerializable(typeof(List<PinnedItem>))]
[JsonSerializable(typeof(List<string>))] // legacy format migration
internal sealed partial class FavoritesJsonContext : JsonSerializerContext;

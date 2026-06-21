using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// JSON-backed set of the instruments the user tracks, persisted under the CmdPal settings folder so
// it survives reloads. Each entry carries two INDEPENDENT membership flags:
//   InWatchlist — shows on the Watchlist screen.
//   IsFavorite  — shows on the Favorites screen AND the dock band.
// An instrument can be on either list, both, or (transiently) neither — an entry whose flags both
// fall to false is dropped. Stores full identity (symbol + name + category), not just the symbol,
// so an instrument added via search — which isn't in InstrumentCatalog — can still be re-priced on
// later loads. Replaces the old FavoritesStore, which conflated "tracked" with "favorite".
internal sealed class WatchlistStore
{
    public static readonly WatchlistStore Instance = new();

    // Raised after a favorite flag flips (and is persisted). Args-less — listeners just re-read Favorites.
    // Lets the pinned dock band refresh immediately instead of going stale until reopened.
    public event Action? FavoritesChanged;

    private readonly Lock _lock = new();
    // Keyed by normalized symbol; value carries the identity plus the two membership flags.
    private Dictionary<string, Entry> _entries = [];

    private sealed class Entry(DomainInstrument instrument)
    {
        public DomainInstrument Instrument { get; } = instrument;
        public bool InWatchlist { get; set; }
        public bool IsFavorite { get; set; }
    }

    private static string JsonPath => Path.Combine(SettingsDirectory, "market_watchlist.json");
    private static string LegacyFavoritesPath => Path.Combine(SettingsDirectory, "market_favorites.json");

    private static string SettingsDirectory
    {
        get
        {
            var directory = Utilities.BaseSettingsPath("Microsoft.CmdPal");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private WatchlistStore() => Load();

    public bool IsInWatchlist(string symbol)
    {
        lock (_lock) return _entries.TryGetValue(Normalize(symbol), out var e) && e.InWatchlist;
    }

    public bool IsFavorite(string symbol)
    {
        lock (_lock) return _entries.TryGetValue(Normalize(symbol), out var e) && e.IsFavorite;
    }

    // The watchlisted instruments, in insertion order, as provider-agnostic identities.
    public IReadOnlyList<DomainInstrument> Watchlist
    {
        get { lock (_lock) return [.. _entries.Values.Where(e => e.InWatchlist).Select(e => e.Instrument)]; }
    }

    // The favorited instruments (the dock subset), in insertion order.
    public IReadOnlyList<DomainInstrument> Favorites
    {
        get { lock (_lock) return [.. _entries.Values.Where(e => e.IsFavorite).Select(e => e.Instrument)]; }
    }

    public void AddToWatchlist(DomainInstrument instrument) => SetFlag(instrument, watchlist: true);
    public void RemoveFromWatchlist(string symbol) => SetFlag(symbol, watchlist: false);
    public void AddToFavorites(DomainInstrument instrument) => SetFlag(instrument, favorite: true);
    public void RemoveFromFavorites(string symbol) => SetFlag(symbol, favorite: false);

    private void SetFlag(string symbol, bool? watchlist = null, bool? favorite = null)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(Normalize(symbol), out var entry))
                Apply(entry, watchlist, favorite);
        }

        Save();
        // Fire outside the lock: the dock's handler re-reads Favorites, which re-acquires it.
        if (favorite is not null) FavoritesChanged?.Invoke();
    }

    private void SetFlag(DomainInstrument instrument, bool? watchlist = null, bool? favorite = null)
    {
        var key = Normalize(instrument.Symbol);
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
                entry = _entries[key] = new Entry(instrument);
            Apply(entry, watchlist, favorite);
        }

        Save();
        if (favorite is not null) FavoritesChanged?.Invoke();
    }

    // Apply a flag change, then drop the entry entirely once it's on neither list.
    private void Apply(Entry entry, bool? watchlist, bool? favorite)
    {
        if (watchlist is { } w) entry.InWatchlist = w;
        if (favorite is { } f) entry.IsFavorite = f;
        if (!entry.InWatchlist && !entry.IsFavorite)
            _entries.Remove(Normalize(entry.Instrument.Symbol));
    }

    private static string Normalize(string symbol) => symbol.Trim().ToUpperInvariant();

    private void Load()
    {
        try
        {
            if (File.Exists(JsonPath))
            {
                var items = JsonSerializer.Deserialize(File.ReadAllText(JsonPath), WatchlistJsonContext.Default.ListWatchlistItem);
                if (items is not null)
                {
                    _entries = items.ToDictionary(
                        i => Normalize(i.Symbol),
                        i => new Entry(new DomainInstrument(i.Symbol, i.Name, i.Category))
                        {
                            InWatchlist = i.InWatchlist,
                            IsFavorite = i.IsFavorite,
                        });
                    return;
                }
            }

            // First run: migrate the old FavoritesStore file if present (its pinned items were both
            // tracked and dock-shown, so they become watchlisted AND favorited), otherwise seed the
            // built-in catalog onto the watchlist. Either way we persist so this is one-time.
            // No FavoritesChanged fires here: this runs in the singleton's ctor, before any subscriber.
            _entries = SeedEntries();
            Save();
        }
        catch
        {
            _entries = [];
        }
    }

    private static Dictionary<string, Entry> SeedEntries()
    {
        if (File.Exists(LegacyFavoritesPath))
        {
            var legacy = JsonSerializer.Deserialize(File.ReadAllText(LegacyFavoritesPath), WatchlistJsonContext.Default.ListPinnedItem);
            if (legacy is { Count: > 0 })
                return legacy.ToDictionary(
                    i => Normalize(i.Symbol),
                    i => new Entry(new DomainInstrument(i.Symbol, i.Name, i.Category)) { InWatchlist = true, IsFavorite = true });
        }

        return InstrumentCatalog.All.ToDictionary(
            i => Normalize(i.Symbol),
            i => new Entry(i) { InWatchlist = true, IsFavorite = false });
    }

    private void Save()
    {
        try
        {
            List<WatchlistItem> snapshot;
            lock (_lock)
                snapshot = [.. _entries.Values.Select(e =>
                    new WatchlistItem(e.Instrument.Symbol, e.Instrument.Name, e.Instrument.Category, e.InWatchlist, e.IsFavorite))];
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(snapshot, WatchlistJsonContext.Default.ListWatchlistItem));
        }
        catch
        {
            // best-effort
        }
    }
}

// Persistence DTO for a tracked instrument and its two membership flags. Source-gen (de)serialized so
// the trimmed Release build stays clean (reflection JSON is the IL2026/IL3050 debt flagged in CLAUDE.md).
internal sealed record WatchlistItem(string Symbol, string Name, AssetCategory Category, bool InWatchlist, bool IsFavorite);

// Legacy persistence DTO from the old FavoritesStore — read once for first-run migration only.
internal sealed record PinnedItem(string Symbol, string Name, AssetCategory Category);

// ⚠️ Keep ALL [JsonSerializable] on this single declaration — splitting them across partials
// silently breaks the JSON source generator (see CLAUDE.md AOT/trim gotcha).
[JsonSourceGenerationOptions(UseStringEnumConverter = true)] // category as readable, reorder-safe text
[JsonSerializable(typeof(List<WatchlistItem>))]
[JsonSerializable(typeof(List<PinnedItem>))] // legacy market_favorites.json migration
internal sealed partial class WatchlistJsonContext : JsonSerializerContext;

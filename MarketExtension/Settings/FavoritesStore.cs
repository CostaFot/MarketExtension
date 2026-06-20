using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// JSON-backed set of favorited instrument symbols, persisted under the CmdPal settings folder
// so favorites survive reloads. Adapted from reference/settings/FavoritesStore.cs.
internal sealed class FavoritesStore
{
    public static readonly FavoritesStore Instance = new();

    private readonly Lock _lock = new();
    private HashSet<string> _favorites = [];

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
        lock (_lock) return _favorites.Contains(Normalize(symbol));
    }

    public void Toggle(string symbol)
    {
        var key = Normalize(symbol);
        lock (_lock)
        {
            if (!_favorites.Remove(key))
                _favorites.Add(key);
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
            var ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids != null)
                _favorites = [.. ids];
        }
        catch
        {
            _favorites = [];
        }
    }

    private void Save()
    {
        try
        {
            List<string> snapshot;
            lock (_lock) snapshot = [.. _favorites];
            File.WriteAllText(JsonPath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // best-effort
        }
    }
}

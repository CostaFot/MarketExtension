using System.Collections.Generic;
using System.Linq;

namespace MarketExtension;

// The live set of instruments the extension prices: the built-in InstrumentCatalog defaults plus
// whatever the user has pinned via search, deduped by symbol (catalog wins on collision). This is
// the single source of truth shared by MarketsPage and FavoritesDockPage so a searched-and-pinned
// instrument shows in both. It supersedes InstrumentCatalog.All as the "what to price" list.
internal static class Watchlist
{
    public static IReadOnlyList<DomainInstrument> Instruments() =>
        InstrumentCatalog.All
            .Concat(FavoritesStore.Instance.Pinned)
            .GroupBy(i => i.Symbol, System.StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
}

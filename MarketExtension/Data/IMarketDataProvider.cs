using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// One market-data source (Finnhub, a future forex source, the offline mock, ...). Each provider
// declares which asset classes it can serve and maps its own Api* response into provider-agnostic
// DomainQuotes. MarketRepository coordinates one or more of these; the UI depends on the
// repository, not on a provider directly.
internal interface IMarketDataProvider
{
    // Which asset classes this source can price — used by MarketRepository to route instruments.
    bool Supports(AssetCategory category);

    Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default);

    // Look up instruments by free-text query (symbol or name). Returns identity only — no prices,
    // so a search costs one call regardless of how many matches come back. A provider that can't
    // search returns an empty list. MarketRepository fans out and merges across providers.
    Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default);
}

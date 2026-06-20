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
}

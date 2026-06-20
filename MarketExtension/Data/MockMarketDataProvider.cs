using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// Offline/dev fallback source. Returns deterministic seed prices for the requested instruments so
// the UI works without network access. No longer the active provider (FinnhubMarketDataProvider
// is) — kept to prove the seam stays swappable and as a no-network fallback. Supports every asset
// class.
internal sealed class MockMarketDataProvider : IMarketDataProvider
{
    private static readonly Dictionary<string, (decimal Price, decimal Change, decimal Pct)> Seed = new()
    {
        ["AAPL"] = (189.20m, 2.24m, 1.20m),
        ["MSFT"] = (421.10m, 1.68m, 0.40m),
        ["NVDA"] = (118.45m, -3.10m, -2.55m),
        ["BTC"] = (64210.00m, -512.00m, -0.80m),
        ["ETH"] = (3420.50m, 45.20m, 1.34m),
        ["SOL"] = (148.30m, -2.10m, -1.40m),
        ["EURUSD"] = (1.0832m, -0.0011m, -0.10m),
        ["GBPUSD"] = (1.2715m, 0.0024m, 0.19m),
        ["USDJPY"] = (157.420m, 0.310m, 0.20m),
    };

    public bool Supports(AssetCategory category) => true;

    public Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        IReadOnlyList<DomainQuote> quotes =
        [
            .. instruments.Select(i => Seed.TryGetValue(i.Symbol, out var s)
                ? new DomainQuote(i.Symbol, i.Name, i.Category, s.Price, s.Change, s.Pct, IsValid: true)
                : new DomainQuote(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false)),
        ];
        return Task.FromResult(quotes);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// The "Demo mode" data source. Returns deterministic seed prices/candles for the requested instruments so
// the whole UI works with no API key and no network. Registered FIRST in the repository and, in Demo mode,
// declared EXCLUSIVE (IsExclusive) so it takes precedence EVERYWHERE — the repository routes quotes, candles,
// AND the search fan-out to it alone, so no live provider is consulted at all (closing the search-leak that
// Supports() can't gate). Off, it's neither exclusive nor (via Supports) selectable, so live installs fall
// straight through to the real providers; flipping the setting applies without a reload. Each seed entry
// carries its real name, category, and NATIVE currency so search returns correctly-typed results and quotes
// mirror the live providers' currency stamping (incl. a non-USD stock, to exercise multi-currency conversion
// + total return offline).
internal sealed class MockMarketDataProvider : IMarketDataProvider
{
    private readonly record struct SeedQuote(
        string Name, AssetCategory Category, decimal Price, decimal Change, decimal Pct, string Currency);

    private static readonly Dictionary<string, SeedQuote> Seed = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["AAPL"]   = new("Apple Inc.",                AssetCategory.Stock,    189.20m,   2.24m,  1.20m, "USD"),
        ["MSFT"]   = new("Microsoft Corp.",           AssetCategory.Stock,    421.10m,   1.68m,  0.40m, "USD"),
        ["NVDA"]   = new("NVIDIA Corp.",              AssetCategory.Stock,    118.45m,  -3.10m, -2.55m, "USD"),
        // A London-listed stock priced in GBP — gives multi-currency conversion + total return a non-USD
        // native to exercise offline (the live GBX/pence path is normalized to GBP by the real providers).
        ["HSBA.L"] = new("HSBC Holdings plc",         AssetCategory.Stock,      6.50m,   0.08m,  1.25m, "GBP"),
        ["BTC"]    = new("Bitcoin",                   AssetCategory.Crypto, 64210.00m, -512.00m, -0.80m, "USD"),
        ["ETH"]    = new("Ethereum",                  AssetCategory.Crypto,  3420.50m,  45.20m,  1.34m, "USD"),
        ["SOL"]    = new("Solana",                    AssetCategory.Crypto,   148.30m,  -2.10m, -1.40m, "USD"),
        ["EURUSD"] = new("Euro / US Dollar",          AssetCategory.Currency,   1.0832m, -0.0011m, -0.10m, "USD"),
        ["GBPUSD"] = new("British Pound / US Dollar", AssetCategory.Currency,   1.2715m,  0.0024m,  0.19m, "USD"),
        ["USDJPY"] = new("US Dollar / Japanese Yen",  AssetCategory.Currency, 157.420m,   0.310m,   0.20m, "JPY"),
    };

    // Gated on Demo mode: when off this provider serves nothing (Supports false → the repository's
    // first-match routing skips it for quotes/candles and falls through to the real providers); when on it
    // serves every asset class.
    public bool Supports(AssetCategory category) => MarketSettingsManager.Instance.DemoMode;

    // In Demo mode, be the SOLE active source so the mock wins everywhere — including the search fan-out the
    // repository would otherwise send to live providers too (which Supports() can't gate). Off → not
    // exclusive, so the real providers serve as normal.
    public bool IsExclusive => MarketSettingsManager.Instance.DemoMode;

    public Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Self-gate on Demo mode: when ON the mock is exclusive, so the repository sends search to it alone;
        // when OFF it's still in the repository's search fan-out (exclusivity doesn't apply), so returning []
        // keeps the seed from polluting live results.
        query = MarketSettingsManager.Instance.DemoMode ? query?.Trim() ?? string.Empty : string.Empty;
        IReadOnlyList<DomainInstrument> matches = query.Length == 0
            ? []
            // Match on symbol OR name, return each result with its REAL category (not all-Stock), ordered
            // for deterministic test output.
            : [.. Seed
                .Where(kv => kv.Key.Contains(query, System.StringComparison.OrdinalIgnoreCase)
                          || kv.Value.Name.Contains(query, System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, System.StringComparer.Ordinal)
                .Select(kv => new DomainInstrument(kv.Key, kv.Value.Name, kv.Value.Category))];
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        IReadOnlyList<DomainQuote> quotes =
        [
            // Use the incoming instrument's identity (name/category) but the seed's price + native currency,
            // so e.g. HSBA.L prices in GBP exactly like a real London quote would.
            .. instruments.Select(i => Seed.TryGetValue(i.Symbol, out var s)
                ? new DomainQuote(i.Symbol, i.Name, i.Category, s.Price, s.Change, s.Pct, IsValid: true,
                    Currency: s.Currency)
                : new DomainQuote(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false)),
        ];
        return Task.FromResult(quotes);
    }

    public Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        // A deterministic random walk anchored on the seed price (or a default) so the chart renders
        // offline / on a free key. Point count varies by range for a believable shape; seeded by
        // (symbol, range) so the same chart is stable across re-fetches.
        var basePrice = Seed.TryGetValue(instrument.Symbol, out var s) && s.Price > 0 ? s.Price : 100m;
        var count = range switch
        {
            ChartRange.OneDay => 78,
            ChartRange.OneWeek => 65,
            ChartRange.OneMonth => 120,
            ChartRange.OneYear => 252,
            ChartRange.FiveYear => 260,
            _ => 100,
        };

        var rng = new Random(HashCode.Combine(instrument.Symbol, range));
        var step = range.Lookback() / count;
        var now = DateTimeOffset.UtcNow;
        var price = (double)basePrice * 0.92; // start below the seed so the walk trends toward it
        var points = new List<CandlePoint>(count);
        for (var i = 0; i < count; i++)
        {
            price *= 1 + ((rng.NextDouble() - 0.48) * 0.02); // small per-step move, slight upward drift
            points.Add(new CandlePoint(now - (step * (count - i)), (decimal)price));
        }

        IReadOnlyList<CandlePoint> pts = points;
        return Task.FromResult(new DomainCandleSeries(instrument.Symbol, range, pts, IsValid: true));
    }
}

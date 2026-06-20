using System.Collections.Generic;

namespace MarketExtension;

// Placeholder data source. Returns a fixed set of plausible quotes across all three
// categories so the UI (and later the Dock band) has something realistic to render before
// the real API exists. Replace this class with an API-backed IMarketDataProvider later.
internal sealed class MockMarketDataProvider : IMarketDataProvider
{
    public IReadOnlyList<Quote> GetQuotes() =>
    [
        // Stocks
        new Quote("AAPL",   "Apple Inc.",        AssetCategory.Stock,    189.20m,   2.24m,  1.20m),
        new Quote("MSFT",   "Microsoft Corp.",   AssetCategory.Stock,    421.10m,   1.68m,  0.40m),
        new Quote("NVDA",   "NVIDIA Corp.",      AssetCategory.Stock,    118.45m,  -3.10m, -2.55m),

        // Crypto
        new Quote("BTC",    "Bitcoin",           AssetCategory.Crypto, 64210.00m, -512.00m, -0.80m),
        new Quote("ETH",    "Ethereum",          AssetCategory.Crypto,  3420.50m,   45.20m,  1.34m),
        new Quote("SOL",    "Solana",            AssetCategory.Crypto,   148.30m,   -2.10m, -1.40m),

        // Currency
        new Quote("EURUSD", "Euro / US Dollar",  AssetCategory.Currency,  1.0832m, -0.0011m, -0.10m),
        new Quote("GBPUSD", "Pound / US Dollar", AssetCategory.Currency,  1.2715m,  0.0024m,  0.19m),
        new Quote("USDJPY", "US Dollar / Yen",   AssetCategory.Currency, 157.420m,  0.310m,   0.20m),
    ];
}

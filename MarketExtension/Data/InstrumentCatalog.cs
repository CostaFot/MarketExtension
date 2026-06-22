using System.Collections.Generic;

namespace MarketExtension;

// The static set of instruments the extension shows. Provider-agnostic (neutral tickers);
// whichever IMarketDataProvider is active turns these into live Quotes. This is app config,
// not provider state, so the pages pass it in. Later this gives way to a user-pinned list.
//
// FX pairs are served by FrankfurterMarketDataProvider (keyless ECB daily rates) — Finnhub's free
// tier gates OANDA:* /quote behind a paid plan, so forex is routed to Frankfurter via the
// IMarketDataProvider seam. Pairs use the neutral 6-letter form BASE+QUOTE (EURUSD), which the
// provider splits into base/quote currencies.
internal static class InstrumentCatalog
{
    public static readonly IReadOnlyList<DomainInstrument> All =
    [
        // Stocks
        new("AAPL", "Apple Inc.",      AssetCategory.Stock),
        new("MSFT", "Microsoft Corp.", AssetCategory.Stock),
        new("NVDA", "NVIDIA Corp.",    AssetCategory.Stock),

        // Crypto
        new("BTC",  "Bitcoin",         AssetCategory.Crypto),
        new("ETH",  "Ethereum",        AssetCategory.Crypto),
        new("SOL",  "Solana",          AssetCategory.Crypto),

        // Currency (Forex) — priced via Frankfurter (keyless ECB daily fixings).
        new("EURUSD", "Euro / US Dollar",           AssetCategory.Currency),
        new("GBPUSD", "British Pound / US Dollar",   AssetCategory.Currency),
        new("USDJPY", "US Dollar / Japanese Yen",    AssetCategory.Currency),
    ];
}

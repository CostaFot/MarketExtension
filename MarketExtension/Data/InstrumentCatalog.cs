using System.Collections.Generic;

namespace MarketExtension;

// The static set of instruments the extension shows. Provider-agnostic (neutral tickers);
// whichever IMarketDataProvider is active turns these into live Quotes. This is app config,
// not provider state, so the pages pass it in. Later this gives way to a user-pinned list.
//
// NOTE: Forex pairs are intentionally omitted for now — Finnhub's free tier gates /quote for
// OANDA:* behind a paid plan ("You don't have access to this resource."). AssetCategory.Currency
// and the providers' symbol formatting stay in place, so FX can be re-added in one spot once we
// either pay for Finnhub forex or slot in a free keyless FX provider (e.g. Frankfurter) behind
// the same IMarketDataProvider seam.
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

        // Currency (Forex) — omitted until a forex-capable provider is wired; see note above.
    ];
}

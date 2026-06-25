namespace MarketExtension;

// Domain layer: the provider-agnostic identity of a tradable instrument — what to price, not how
// to fetch it. The Symbol is the neutral ticker the app knows ("BTC", "AAPL", "EURUSD"); each
// IMarketDataProvider translates it into its own API's symbol format.
internal sealed record DomainInstrument(string Symbol, string Name, AssetCategory Category);

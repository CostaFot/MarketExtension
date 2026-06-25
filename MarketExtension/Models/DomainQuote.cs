namespace MarketExtension;

// Domain layer: a provider-agnostic, presentation-free quote. This is what every
// IMarketDataProvider produces (mapped from its Api* DTO) and what MarketRepository returns.
// No formatting lives here — see UiQuote for the presentation projection.
//
// Currency is the ISO-4217 code the Price/Change are quoted in (e.g. "USD", "GBP", "JPY") — the native
// currency of the instrument, NOT the user's reporting currency. It's STATIC metadata each provider
// resolves once: Twelve Data returns it on /quote, Finnhub via a cached /stock/profile2 lookup,
// Frankfurter from the pair's quote currency; crypto and US equities short-circuit to USD. Providers
// normalize minor-unit quotes (London's GBX/pence → GBP, dividing by 100) so Price is always in major
// units of Currency. Defaults to "USD" so every existing construction stays correct without change.
// PortfolioPage converts Price×Quantity from Currency into the user's PortfolioCurrency (via
// CurrencyConverter); nothing else interprets it beyond UiQuote picking the right currency symbol.
internal sealed record DomainQuote(
    string Symbol,
    string Name,
    AssetCategory Category,
    decimal Price,
    decimal Change,
    decimal ChangePercent,
    bool IsValid = true,
    string Currency = "USD");

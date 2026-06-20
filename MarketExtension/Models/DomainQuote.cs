namespace MarketExtension;

// Domain layer: a provider-agnostic, presentation-free quote. This is what every
// IMarketDataProvider produces (mapped from its Api* DTO) and what MarketRepository returns.
// No formatting lives here — see UiQuote for the presentation projection.
internal sealed record DomainQuote(
    string Symbol,
    string Name,
    AssetCategory Category,
    decimal Price,
    decimal Change,
    decimal ChangePercent,
    bool IsValid = true);

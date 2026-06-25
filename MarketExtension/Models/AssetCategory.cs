namespace MarketExtension;

// The asset class shared across all three model layers (Api / Domain / Ui). It's the common
// vocabulary, not a model DTO, so it keeps its plain unprefixed name.
internal enum AssetCategory
{
    Stock,
    Crypto,
    Currency,
}

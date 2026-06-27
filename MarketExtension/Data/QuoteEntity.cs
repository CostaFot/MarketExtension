namespace MarketExtension;

// Data layer (the cache data source's storage model): a structural mirror of DomainQuote that lives BELOW
// the repository. IQuoteCacheDataSource stores and emits QuoteEntity, never DomainQuote, so the storage
// model never escapes the data source — MarketRepository maps QuoteEntity <-> DomainQuote at its boundary
// (From on write-through, ToDomainQuote on read). It's deliberately identical to DomainQuote today; keeping
// it a separate type means the cache's stored shape can diverge from the domain model later (storage-only
// fields, a different on-disk representation for a DB-backed source) without touching the domain layer or
// any surface. See DomainQuote for the field semantics (Currency, IsValid, the GBX/pence rule).
//
// Data → domain is the allowed dependency direction, so this type may reference DomainQuote for mapping;
// DomainQuote must never reference QuoteEntity. The mappers live here (like UiQuote.From) but are called
// only by the repository — "do the mapping at the boundary".
internal sealed record QuoteEntity(
    string Symbol,
    string Name,
    AssetCategory Category,
    decimal Price,
    decimal Change,
    decimal ChangePercent,
    bool IsValid = true,
    string Currency = "USD")
{
    // Domain → storage. The single map-IN seam; the repository calls this on write-through.
    public static QuoteEntity From(DomainQuote q) =>
        new(q.Symbol, q.Name, q.Category, q.Price, q.Change, q.ChangePercent, q.IsValid, q.Currency);

    // Storage → domain. The single map-OUT seam; the repository calls this on read so the entity never
    // escapes the data source.
    public DomainQuote ToDomainQuote() =>
        new(Symbol, Name, Category, Price, Change, ChangePercent, IsValid, Currency);
}

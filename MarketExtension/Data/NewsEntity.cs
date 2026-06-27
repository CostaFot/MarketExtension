using System;

namespace MarketExtension;

// Data layer (the news cache data source's storage model): a structural mirror of DomainNews that lives
// BELOW the repository. INewsCacheDataSource stores and emits NewsEntity, never DomainNews, so the storage
// model never escapes the data source — MarketRepository maps NewsEntity <-> DomainNews at its boundary
// (From on write-through, ToDomainNews on read). It's deliberately identical to DomainNews today; keeping it
// a separate type means the cache's stored shape can diverge from the domain model later (storage-only
// fields, a different on-disk representation for a DB-backed source) without touching the domain layer or
// any surface. See DomainNews for the field semantics.
//
// Data → domain is the allowed dependency direction, so this type may reference DomainNews for mapping;
// DomainNews must never reference NewsEntity. The mappers live here (like QuoteEntity.From) but are called
// only by the repository — "do the mapping at the boundary".
internal sealed record NewsEntity(
    long Id,
    string Headline,
    string Source,
    string Summary,
    string Category,
    string ArticleUrl,
    DateTimeOffset Published,
    string? ImageUrl = null,
    string? Related = null)
{
    // Domain → storage. The single map-IN seam; the repository calls this on write-through.
    public static NewsEntity From(DomainNews n) =>
        new(n.Id, n.Headline, n.Source, n.Summary, n.Category, n.ArticleUrl, n.Published, n.ImageUrl, n.Related);

    // Storage → domain. The single map-OUT seam; the repository calls this on read so the entity never
    // escapes the data source.
    public DomainNews ToDomainNews() =>
        new(Id, Headline, Source, Summary, Category, ArticleUrl, Published, ImageUrl, Related);
}

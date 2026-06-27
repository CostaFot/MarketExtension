using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of one item in Finnhub's GET /news (market news) response. Internal to the
// Finnhub provider, which will map it into a provider-agnostic domain model. The /news endpoint returns
// a BARE top-level JSON array of these objects (unlike /search, which wraps its results in an object),
// so the serializable root registered on the context is ApiFinnhubNewsDto[] — see below. All fields are
// nullable because Finnhub omits them for sparse articles (e.g. an empty `related`, a missing image);
// the provider coalesces as needed.
//
// Source-generated (de)serialization keeps the AOT/trim build clean — reflection-based JSON would trip
// ILLinkTreatWarningsAsErrors. Registration lives on the single FinnhubJsonContext declaration in
// ApiFinnhubQuoteDto.cs: the JSON source generator does NOT support [JsonSerializable] attributes split
// across multiple partial declarations (it emits colliding hintNames and fails), so all serializable
// types for that context must be attributed in one place.
internal sealed record ApiFinnhubNewsDto(
    [property: JsonPropertyName("category")] string? Category, // news category, e.g. "technology"
    [property: JsonPropertyName("datetime")] long? Datetime,   // published time, UNIX seconds
    [property: JsonPropertyName("headline")] string? Headline, // article headline
    [property: JsonPropertyName("id")]       long? Id,         // news id; reusable as minId for the next fetch
    [property: JsonPropertyName("image")]    string? Image,    // thumbnail image URL
    [property: JsonPropertyName("related")]  string? Related,  // related stocks/companies mentioned
    [property: JsonPropertyName("source")]   string? Source,   // news source, e.g. "CNBC"
    [property: JsonPropertyName("summary")]  string? Summary,  // article summary
    [property: JsonPropertyName("url")]      string? Url);     // URL of the original article

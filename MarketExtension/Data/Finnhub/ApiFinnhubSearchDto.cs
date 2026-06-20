using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Finnhub's GET /search (symbol lookup) response. Internal to the
// Finnhub provider, which maps each result into a provider-agnostic DomainInstrument. All fields
// are nullable because Finnhub omits them for sparse matches; the provider skips results without a
// symbol.
//
// Source-generated (de)serialization keeps the AOT/trim build clean — reflection-based JSON would
// trip ILLinkTreatWarningsAsErrors. Registration lives on the single FinnhubJsonContext
// declaration in ApiFinnhubQuoteDto.cs: the JSON source generator does NOT support [JsonSerializable]
// attributes split across multiple partial declarations (it emits colliding hintNames and fails),
// so all serializable types for that context must be attributed in one place.
internal sealed record ApiFinnhubSearchDto(
    [property: JsonPropertyName("count")]  int Count,
    [property: JsonPropertyName("result")] IReadOnlyList<ApiFinnhubSearchResultDto>? Result);

internal sealed record ApiFinnhubSearchResultDto(
    [property: JsonPropertyName("description")]   string? Description,   // e.g. "APPLE INC"
    [property: JsonPropertyName("displaySymbol")] string? DisplaySymbol, // UI symbol, e.g. "AAPL"
    [property: JsonPropertyName("symbol")]        string? Symbol,        // canonical id for /quote
    [property: JsonPropertyName("type")]          string? Type);         // e.g. "Common Stock"

using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Finnhub's GET /quote response. Internal to the Finnhub provider,
// which maps it into a DomainQuote. Fields are nullable because Finnhub returns null for
// change/percent on instruments without a previous close; the provider coalesces to 0.
//
// Source-generated (de)serialization keeps the AOT/trim build clean: reflection-based JSON would
// trip ILLinkTreatWarningsAsErrors.
internal sealed record ApiFinnhubQuoteDto(
    [property: JsonPropertyName("c")]  decimal? Current,        // current price
    [property: JsonPropertyName("d")]  decimal? Change,         // absolute change
    [property: JsonPropertyName("dp")] decimal? ChangePercent,  // percent change
    [property: JsonPropertyName("pc")] decimal? PreviousClose); // previous close

[JsonSerializable(typeof(ApiFinnhubQuoteDto))]
[JsonSerializable(typeof(ApiFinnhubSearchDto))] // /search response — see ApiFinnhubSearchDto.cs
[JsonSerializable(typeof(ApiFinnhubCandleDto))] // /stock/candle + /crypto/candle — see ApiFinnhubCandleDto.cs
internal sealed partial class FinnhubJsonContext : JsonSerializerContext;

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Twelve Data's GET /symbol_search response. Internal to the Twelve Data
// provider, which maps each result into a provider-agnostic DomainInstrument — deriving the asset
// class from instrument_type and normalizing the symbol back to our neutral convention (crypto -> bare
// base coin, FX -> 6-letter pair) so it round-trips through the later quote/candle fetch. Fields are
// nullable because Twelve Data omits them for sparse matches; the provider skips results without a symbol.
//
// Registered on TwelveDataJsonContext via the SINGLE [JsonSerializable] declaration in
// ApiTwelveDataQuoteDto.cs — never a second partial (see the source-gen gotcha in CLAUDE.md).
internal sealed record ApiTwelveDataSearchDto(
    [property: JsonPropertyName("data")]   IReadOnlyList<ApiTwelveDataSearchResultDto>? Data,
    [property: JsonPropertyName("status")] string? Status);

internal sealed record ApiTwelveDataSearchResultDto(
    [property: JsonPropertyName("symbol")]          string? Symbol,          // e.g. "AAPL", "BTC/USD", "EUR/USD"
    [property: JsonPropertyName("instrument_name")] string? InstrumentName,  // e.g. "Apple Inc"
    [property: JsonPropertyName("instrument_type")] string? InstrumentType,  // e.g. "Common Stock", "Digital Currency"
    [property: JsonPropertyName("exchange")]        string? Exchange,
    [property: JsonPropertyName("currency")]        string? Currency);

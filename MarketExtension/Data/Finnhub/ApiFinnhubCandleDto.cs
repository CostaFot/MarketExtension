using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Finnhub's GET /stock/candle (and /crypto/candle — identical shape)
// response. Internal to the Finnhub provider, which maps it into a DomainCandleSeries. The arrays are
// column-oriented and parallel by index: Close[i]/Timestamps[i] are candle i's close and time. Status
// is "ok" or "no_data". Open/High/Low/Volume are parsed but currently unused (kept for a future OHLC
// tooltip). Premium endpoint — a free-tier key gets a 403 (handled in the provider).
//
// Registered on FinnhubJsonContext via the SINGLE [JsonSerializable] declaration in
// ApiFinnhubQuoteDto.cs — never a second partial declaration (see the source-gen gotcha in CLAUDE.md).
internal sealed record ApiFinnhubCandleDto(
    [property: JsonPropertyName("c")] IReadOnlyList<decimal>? Close,
    [property: JsonPropertyName("h")] IReadOnlyList<decimal>? High,
    [property: JsonPropertyName("l")] IReadOnlyList<decimal>? Low,
    [property: JsonPropertyName("o")] IReadOnlyList<decimal>? Open,
    [property: JsonPropertyName("t")] IReadOnlyList<long>? Timestamps,
    [property: JsonPropertyName("v")] IReadOnlyList<decimal>? Volume,
    [property: JsonPropertyName("s")] string? Status);

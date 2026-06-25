using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Twelve Data's GET /time_series response. Internal to the Twelve Data
// provider, which maps it into a DomainCandleSeries. Unlike Finnhub's column-oriented candle arrays,
// Twelve Data returns a row per bar in `values` (newest-first — the provider reverses to chronological
// order). Status is "ok" or "error" (with code/message). Open/High/Low/Volume are parsed but currently
// unused (kept for a future OHLC tooltip, mirroring ApiFinnhubCandleDto). Unlike Finnhub's candles this
// endpoint is on the FREE tier, so a free key renders real charts.
//
// Numbers arrive as JSON strings; the context's NumberHandling = AllowReadingFromString (in
// ApiTwelveDataQuoteDto.cs) parses them into decimal?. Registered on TwelveDataJsonContext via the
// SINGLE [JsonSerializable] declaration in ApiTwelveDataQuoteDto.cs — never a second partial (see the
// source-gen gotcha in CLAUDE.md).
internal sealed record ApiTwelveDataTimeSeriesDto(
    [property: JsonPropertyName("values")]  IReadOnlyList<ApiTwelveDataValueDto>? Values,
    [property: JsonPropertyName("status")]  string? Status,
    [property: JsonPropertyName("code")]    int? Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ApiTwelveDataValueDto(
    [property: JsonPropertyName("datetime")] string? Datetime, // "2021-09-16 15:59:00" or "2021-09-16"
    [property: JsonPropertyName("open")]     decimal? Open,
    [property: JsonPropertyName("high")]     decimal? High,
    [property: JsonPropertyName("low")]      decimal? Low,
    [property: JsonPropertyName("close")]    decimal? Close,
    [property: JsonPropertyName("volume")]   decimal? Volume);

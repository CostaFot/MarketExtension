using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Twelve Data's GET /quote response. Internal to the Twelve Data
// provider, which maps it into a DomainQuote. A successful quote carries close/change/percent_change;
// a per-symbol failure (unknown ticker, symbol not on the plan) comes back as the same object with
// status="error" + code/message instead, so those fields are nullable and the provider treats a
// missing/zero close as invalid.
//
// Twelve Data encodes numbers as JSON strings ("148.85001"); the context's
// NumberHandling = AllowReadingFromString lets them deserialize straight into decimal? — no manual
// parsing. Source-generated (de)serialization keeps the AOT/trim build clean.
//
// ⚠️ Per the CLAUDE.md source-gen gotcha, ALL [JsonSerializable] for this context live on the SINGLE
// partial declaration below (splitting across partials silently breaks the whole generator). The
// candle and search DTOs (separate files) are registered here, not in their own files.
internal sealed record ApiTwelveDataQuoteDto(
    [property: JsonPropertyName("symbol")]         string? Symbol,
    [property: JsonPropertyName("name")]           string? Name,
    [property: JsonPropertyName("close")]          decimal? Close,         // current price
    [property: JsonPropertyName("change")]         decimal? Change,        // absolute day change
    [property: JsonPropertyName("percent_change")] decimal? PercentChange, // percent day change
    [property: JsonPropertyName("currency")]       string? Currency,       // native currency of the quote (e.g. "USD", "GBp")
    [property: JsonPropertyName("status")]         string? Status,         // "error" on a per-symbol failure
    [property: JsonPropertyName("code")]           int? Code,              // error code when status=="error"
    [property: JsonPropertyName("message")]        string? Message);

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ApiTwelveDataQuoteDto))]
[JsonSerializable(typeof(Dictionary<string, ApiTwelveDataQuoteDto>))] // multi-symbol /quote response (keyed by symbol)
[JsonSerializable(typeof(ApiTwelveDataTimeSeriesDto))]                // /time_series — see ApiTwelveDataCandleDto.cs
[JsonSerializable(typeof(ApiTwelveDataSearchDto))]                    // /symbol_search — see ApiTwelveDataSearchDto.cs
internal sealed partial class TwelveDataJsonContext : JsonSerializerContext;

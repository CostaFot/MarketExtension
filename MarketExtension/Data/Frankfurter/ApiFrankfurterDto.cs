using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the raw shape of Frankfurter's time-series response
//   GET /v1/{from}..{to}?base=EUR&symbols=USD
//   { "amount":1.0, "base":"EUR", "start_date":"…", "end_date":"…",
//     "rates": { "2024-06-03": { "USD": 1.0842 }, … } }
// `rates` is keyed by ISO date, each mapping a currency code → that day's rate (per 1 base unit).
// Internal to FrankfurterMarketDataProvider, which maps it into DomainQuotes / DomainCandleSeries.
// One DTO covers both flows: a short window backs the day-over-day quote, a wider one backs the chart.
//
// Source-generated (de)serialization keeps the AOT/trim build clean. Per the CLAUDE.md gotcha, the
// single [JsonSerializable] attribute lives on one partial declaration of this context.
internal sealed record ApiFrankfurterSeriesDto(
    [property: JsonPropertyName("amount")]     decimal Amount,
    [property: JsonPropertyName("base")]       string? Base,
    [property: JsonPropertyName("start_date")] string? StartDate,
    [property: JsonPropertyName("end_date")]   string? EndDate,
    [property: JsonPropertyName("rates")]      Dictionary<string, Dictionary<string, decimal>>? Rates);

[JsonSerializable(typeof(ApiFrankfurterSeriesDto))]
internal sealed partial class FrankfurterJsonContext : JsonSerializerContext;

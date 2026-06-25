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

// Api layer: the raw shape of Frankfurter's LATEST (spot) response
//   GET /v1/latest?base=USD&symbols=GBP,JPY
//   { "amount":1.0, "base":"USD", "date":"…", "rates": { "GBP": 0.78, "JPY": 156.2 } }
// Here `rates` is a flat currency → rate map (per 1 base unit), NOT nested by date like the series above.
// Used by CurrencyConverter to convert holding values into the user's PortfolioCurrency.
internal sealed record ApiFrankfurterLatestDto(
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("base")]   string? Base,
    [property: JsonPropertyName("date")]   string? Date,
    [property: JsonPropertyName("rates")]  Dictionary<string, decimal>? Rates);

// ⚠️ Keep ALL [JsonSerializable] on this single declaration — splitting them across partials silently
// breaks the JSON source generator (see CLAUDE.md AOT/trim gotcha).
[JsonSerializable(typeof(ApiFrankfurterSeriesDto))]
[JsonSerializable(typeof(ApiFrankfurterLatestDto))]
internal sealed partial class FrankfurterJsonContext : JsonSerializerContext;

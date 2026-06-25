using System.Text.Json.Serialization;

namespace MarketExtension;

// Api layer: the slice of Finnhub's GET /stock/profile2?symbol= response we care about — the instrument's
// reporting `currency`. Finnhub's /quote (c/d/dp/pc) carries NO currency, so the provider resolves it
// here, ONCE per symbol (currency is static metadata), and caches it forever. US tickers come back "USD";
// a paid plan's non-US listings report their local currency (e.g. "GBp" for London → normalized to GBP).
//
// Registered on FinnhubJsonContext in ApiFinnhubQuoteDto.cs (the single context declaration) per the
// CLAUDE.md source-gen gotcha — NOT in its own context here.
internal sealed record ApiFinnhubProfileDto(
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("ticker")]   string? Ticker);

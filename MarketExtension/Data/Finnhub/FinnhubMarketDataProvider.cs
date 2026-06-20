using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// Live market data from Finnhub (https://finnhub.io). One IMarketDataProvider source: it formats
// each neutral DomainInstrument into Finnhub's symbol syntax, calls /quote, and maps the raw
// ApiFinnhubQuoteDto into a DomainQuote. Nothing outside this class references Finnhub, so a
// different provider can replace it without touching the repository or the UI.
internal sealed class FinnhubMarketDataProvider : IMarketDataProvider
{
    // Loaded from the gitignored secrets.props at build time (see MarketExtension.csproj's
    // GenerateSecrets target + secrets.props.template). TODO: move to per-user settings / BYO-key.
    private const string ApiKey = Secrets.FinnhubApiKey;

    // Reuse a single client (creating one per request exhausts sockets). AOT-safe.
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://finnhub.io/api/v1/") };

    // Finnhub's free tier serves stocks and crypto; forex (OANDA:*) is premium-gated.
    public bool Supports(AssetCategory category) => category is AssetCategory.Stock or AssetCategory.Crypto;

    public async Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        // One /quote call per instrument, fanned out. A handful of symbols stays well under the
        // free-tier 60 calls/minute limit.
        return await Task.WhenAll(instruments.Select(i => FetchQuoteAsync(i, ct))).ConfigureAwait(false);
    }

    private static async Task<DomainQuote> FetchQuoteAsync(DomainInstrument instrument, CancellationToken ct)
    {
        var symbol = ToFinnhubSymbol(instrument);
        try
        {
            // NEVER log the full URL — it carries the API token. Log the symbol only.
            Log.Info("Finnhub", $"GET /quote symbol={symbol}");
            using var response = await Http.GetAsync(
                $"quote?symbol={Uri.EscapeDataString(symbol)}&token={ApiKey}", ct).ConfigureAwait(false);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Info("Finnhub", $"{symbol} <- {(int)response.StatusCode} {response.StatusCode}  body={json}");

            if (!response.IsSuccessStatusCode)
            {
                // e.g. 403 premium-gated (forex), 429 rate-limited.
                Log.Warn("Finnhub", $"{symbol}: non-success status {(int)response.StatusCode} — treating as unavailable");
                return Invalid(instrument);
            }

            var dto = JsonSerializer.Deserialize(json, FinnhubJsonContext.Default.ApiFinnhubQuoteDto);

            // Finnhub returns all-zero fields for unknown/unavailable symbols (not an HTTP error).
            var price = dto?.Current ?? 0m;
            if (price == 0m)
            {
                Log.Warn("Finnhub", $"{symbol}: all-zero quote — treating as invalid symbol");
                return Invalid(instrument);
            }

            var quote = new DomainQuote(
                instrument.Symbol, instrument.Name, instrument.Category,
                price, dto!.Change ?? 0m, dto.ChangePercent ?? 0m, IsValid: true);
            Log.Info("Finnhub", $"{symbol} -> price={quote.Price} change%={quote.ChangePercent}");
            return quote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Network / HTTP / parse failure: degrade to an invalid quote rather than breaking the
            // whole page. Richer error/rate-limit UX is deferred.
            Log.Error("Finnhub", $"{symbol}: request failed", ex);
            return Invalid(instrument);
        }
    }

    private static DomainQuote Invalid(DomainInstrument i) =>
        new(i.Symbol, i.Name, i.Category, 0m, 0m, 0m, IsValid: false);

    // Neutral ticker -> Finnhub symbol syntax. Keeps InstrumentCatalog provider-agnostic.
    //   Stock    AAPL    -> AAPL
    //   Crypto   BTC     -> BINANCE:BTCUSDT
    //   Currency EURUSD  -> OANDA:EUR_USD   (premium on the free tier; see InstrumentCatalog)
    private static string ToFinnhubSymbol(DomainInstrument instrument) => instrument.Category switch
    {
        AssetCategory.Stock    => instrument.Symbol.ToUpperInvariant(),
        AssetCategory.Crypto   => $"BINANCE:{instrument.Symbol.ToUpperInvariant()}USDT",
        AssetCategory.Currency => $"OANDA:{instrument.Symbol.ToUpperInvariant().Insert(3, "_")}",
        _ => instrument.Symbol,
    };
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// One market-data source (Finnhub, a future forex source, the offline mock, ...). Each provider
// declares which asset classes it can serve and maps its own Api* response into provider-agnostic
// DomainQuotes. MarketRepository coordinates one or more of these; the UI depends on the
// repository, not on a provider directly.
internal interface IMarketDataProvider
{
    // Which asset classes this source can price — used by MarketRepository to route instruments.
    bool Supports(AssetCategory category);

    // When true, this provider is the SOLE active source: MarketRepository routes EVERY operation —
    // quotes, candles, AND the search fan-out — to exclusive providers only and ignores the rest. This is
    // how a provider takes precedence everywhere, including search (which Supports() can't gate, since the
    // repository fans search out to all providers). MockMarketDataProvider sets this from the Demo-mode
    // setting so demo data wins across the board. Default false → normal first-match-by-Supports routing; a
    // default member so existing providers opt out for free.
    bool IsExclusive => false;

    Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default);

    // Look up instruments by free-text query (symbol or name). Returns identity only — no prices,
    // so a search costs one call regardless of how many matches come back. A provider that can't
    // search returns an empty list. MarketRepository fans out and merges across providers.
    Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default);

    // Historical price candles for one instrument over a ChartRange (backs the detail-page chart).
    // Provider-agnostic in and out: the provider translates ChartRange.Interval into its own
    // resolution token and ChartRange.Lookback into a from/to window, and maps its Api* candle DTO
    // into a DomainCandleSeries. A provider that can't serve candles keeps this default (an invalid,
    // empty series). MarketRepository routes by asset class, exactly like GetQuotesAsync.
    Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
        => Task.FromResult(DomainCandleSeries.Invalid(instrument.Symbol, range));

    // Whether this provider serves market news (GetNewsAsync). News is a market-wide feed, not
    // per-instrument data routed by Supports(AssetCategory), so it's a SEPARATE capability gate: a
    // caller checks this and only asks a provider that returns true. Default false; a provider opts in
    // by overriding BOTH this and GetNewsAsync (today only Finnhub). Mirrors the IsExclusive
    // default-member pattern, so non-news providers (Twelve Data, Frankfurter, the mock) opt out for free.
    bool SupportsNews => false;

    // Latest market news for a category (the most recent batch; pass a prior item's Id as minId to page
    // forward — see DomainNews). Unlike GetCandlesAsync, the default does NOT soft-degrade to an empty
    // list: news has the SupportsNews gate above, so reaching this default means a caller invoked it on a
    // provider that doesn't serve news — a contract violation surfaced loudly (fail fast) rather than
    // masked as "no news". Check SupportsNews first; a non-news provider throws here.
    Task<IReadOnlyList<DomainNews>> GetNewsAsync(
        NewsCategory category, long minId = 0, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not serve market news (SupportsNews is false); " +
            "check SupportsNews before calling GetNewsAsync.");
}

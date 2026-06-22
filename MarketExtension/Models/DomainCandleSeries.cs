using System;
using System.Collections.Generic;

namespace MarketExtension;

// Domain layer: a provider-agnostic, presentation-free price history for one instrument over one
// ChartRange. Every IMarketDataProvider produces this shape (mapped from its own Api* candle DTO);
// no formatting lives here — see UiCandleSeries for the presentation projection.
internal sealed record DomainCandleSeries(
    string Symbol,
    ChartRange Range,
    IReadOnlyList<CandlePoint> Points,
    bool IsValid = true)
{
    // An empty, invalid series — what providers return when they can't serve candles (no provider
    // for the asset class, premium-gated, "no_data", or a fetch failure). The UI renders this as an
    // "unavailable" state rather than a blank chart.
    public static DomainCandleSeries Invalid(string symbol, ChartRange range) =>
        new(symbol, range, [], IsValid: false);

    public bool HasData => IsValid && Points.Count > 0;

    public decimal FirstClose => Points.Count > 0 ? Points[0].Close : 0m;
    public decimal LastClose => Points.Count > 0 ? Points[^1].Close : 0m;
}

// One sampled point in a series. Close drives the sparkline; the timestamp is kept for future axis
// labels / tooltips. (Full OHLCV lives in the Api DTO and can be promoted here later if needed.)
internal readonly record struct CandlePoint(DateTimeOffset Time, decimal Close);

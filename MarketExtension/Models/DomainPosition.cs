namespace MarketExtension;

// Domain layer: a provider-agnostic portfolio holding — an instrument plus HOW MUCH of it the user
// holds. No prices and no formatting live here (see UiPosition for the presentation projection);
// pricing happens later by routing the Instrument through MarketRepository, exactly like any other
// tracked instrument. Quantity is decimal so fractional crypto holdings (e.g. 0.5 BTC) work.
//
// CostBasis (the average price paid per unit, in the instrument's native currency) is optional: it's set
// from the holding editor and drives total-return (unrealized P&L) reporting in UiPosition/UiPortfolio.
// Null/absent means "not recorded" — total return is simply omitted for that holding.
internal sealed record DomainPosition(DomainInstrument Instrument, decimal Quantity, decimal? CostBasis = null);

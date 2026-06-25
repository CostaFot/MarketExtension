namespace MarketExtension;

// Domain layer: a provider-agnostic portfolio holding — an instrument plus HOW MUCH of it the user
// holds. No prices and no formatting live here (see UiPosition for the presentation projection);
// pricing happens later by routing the Instrument through MarketRepository, exactly like any other
// tracked instrument. Quantity is decimal so fractional crypto holdings (e.g. 0.5 BTC) work.
//
// CostBasis (what was paid per unit) is carried and persisted for forward-compat — it enables
// unrealized/total-return reporting later — but is NOT surfaced in the UI yet (this pass shows daily
// P&L only). It stays null until that feature lands.
internal sealed record DomainPosition(DomainInstrument Instrument, decimal Quantity, decimal? CostBasis = null);

using System.Collections.Generic;

namespace MarketExtension;

// The single seam between the UI and the data source. Today MockMarketDataProvider returns
// static seed data; swapping in a real market-data API later means replacing the
// implementation here and nowhere else.
//
// Kept synchronous to match the existing load convention (the page calls this inside
// Task.Run, the same way the reference AdbExtensionPage wraps AdbHelper.GetInstalledPackages).
internal interface IMarketDataProvider
{
    IReadOnlyList<Quote> GetQuotes();
}

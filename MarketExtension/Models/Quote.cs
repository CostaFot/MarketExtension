using System.Globalization;

namespace MarketExtension;

internal enum AssetCategory
{
    Stock,
    Crypto,
    Currency,
}

// A single market instrument snapshot. This is the model the UI renders today and the future
// Dock band will consume — keep it the shape both need. Today it is populated by
// MockMarketDataProvider; later by a real market-data API behind IMarketDataProvider.
internal sealed record Quote(
    string Symbol,
    string Name,
    AssetCategory Category,
    decimal Price,
    decimal Change,
    decimal ChangePercent)
{
    public bool IsUp => Change >= 0;

    // Currency pairs are quoted to 4 decimals (e.g. 1.0832); stocks/crypto as money.
    public string FormatPrice() => Category == AssetCategory.Currency
        ? Price.ToString("0.0000", CultureInfo.InvariantCulture)
        : Price.ToString("$#,##0.00", CultureInfo.InvariantCulture);

    // e.g. "▲ +1.20%" / "▼ -0.80%"
    public string FormatChange() =>
        $"{(IsUp ? "▲" : "▼")} {ChangePercent.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)}%";
}

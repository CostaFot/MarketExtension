using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketExtension;

// The "Demo mode" data source. Returns deterministic prices/candles for the requested instruments so the
// whole UI works with no API key and no network. Registered FIRST in the repository and, in Demo mode,
// declared EXCLUSIVE (IsExclusive) so it takes precedence EVERYWHERE — the repository routes quotes, candles,
// AND the search fan-out to it alone, so no live provider is consulted at all (closing the search-leak that
// Supports() can't gate). Off, it's neither exclusive nor (via Supports) selectable, so live installs fall
// straight through to the real providers; flipping the setting applies without a reload.
//
// Two data sources back this:
//   * Seed — a handful of headline symbols with hand-tuned, nice-looking prices (used as quote overrides).
//   * Catalog — a curated corpus of REAL instruments (identity only) that backs demo-mode SEARCH. Search
//     returns ONLY real tickers from here — deliberately NOT free-text fabrication — so a user can never add
//     a non-existent symbol to their watchlist/portfolio (those persist to JSON and would show a permanent
//     blank row once demo mode is off). Prices for any tracked symbol (catalog or not) are SYNTHESIZED at
//     quote time from a stable hash of the symbol, with native currency inferred from the symbol shape — so
//     an off-seed real holding added while live still prices in demo mode instead of looking broken.
internal sealed class MockMarketDataProvider : IMarketDataProvider
{
    private readonly record struct SeedQuote(
        string Name, AssetCategory Category, decimal Price, decimal Change, decimal Pct, string Currency);

    // Hand-tuned prices for the headline symbols — these override synthesis so the most-seen instruments
    // look "real" (e.g. AAPL at 189.20, a GBP London stock, a JPY-quoted FX pair). Every key here is also in
    // Catalog so it remains searchable.
    private static readonly Dictionary<string, SeedQuote> Seed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAPL"]   = new("Apple Inc.",                AssetCategory.Stock,    189.20m,   2.24m,  1.20m, "USD"),
        ["MSFT"]   = new("Microsoft Corp.",           AssetCategory.Stock,    421.10m,   1.68m,  0.40m, "USD"),
        ["NVDA"]   = new("NVIDIA Corp.",              AssetCategory.Stock,    118.45m,  -3.10m, -2.55m, "USD"),
        // A London-listed stock priced in GBP — gives multi-currency conversion + total return a non-USD
        // native to exercise offline (the live GBX/pence path is normalized to GBP by the real providers).
        ["HSBA.L"] = new("HSBC Holdings plc",         AssetCategory.Stock,      6.50m,   0.08m,  1.25m, "GBP"),
        ["BTC"]    = new("Bitcoin",                   AssetCategory.Crypto, 64210.00m, -512.00m, -0.80m, "USD"),
        ["ETH"]    = new("Ethereum",                  AssetCategory.Crypto,  3420.50m,  45.20m,  1.34m, "USD"),
        ["SOL"]    = new("Solana",                    AssetCategory.Crypto,   148.30m,  -2.10m, -1.40m, "USD"),
        ["EURUSD"] = new("Euro / US Dollar",          AssetCategory.Currency,   1.0832m, -0.0011m, -0.10m, "USD"),
        ["GBPUSD"] = new("British Pound / US Dollar", AssetCategory.Currency,   1.2715m,  0.0024m,  0.19m, "USD"),
        ["USDJPY"] = new("US Dollar / Japanese Yen",  AssetCategory.Currency, 157.420m,   0.310m,   0.20m, "JPY"),
    };

    private readonly record struct CatalogEntry(string Name, AssetCategory Category);

    // The real-instrument corpus backing demo-mode search. Identity only (name + category); prices are
    // synthesized. Curated, not generated, so search can ONLY surface genuine tickers. Includes every Seed
    // symbol plus a broad spread of well-known stocks (US + international), crypto, and FX pairs.
    private static readonly Dictionary<string, CatalogEntry> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── US large-cap stocks (USD) ──
        ["AAPL"] = new("Apple Inc.", AssetCategory.Stock),
        ["MSFT"] = new("Microsoft Corp.", AssetCategory.Stock),
        ["NVDA"] = new("NVIDIA Corp.", AssetCategory.Stock),
        ["GOOGL"] = new("Alphabet Inc. Class A", AssetCategory.Stock),
        ["GOOG"] = new("Alphabet Inc. Class C", AssetCategory.Stock),
        ["AMZN"] = new("Amazon.com Inc.", AssetCategory.Stock),
        ["META"] = new("Meta Platforms Inc.", AssetCategory.Stock),
        ["TSLA"] = new("Tesla Inc.", AssetCategory.Stock),
        ["AVGO"] = new("Broadcom Inc.", AssetCategory.Stock),
        ["JPM"] = new("JPMorgan Chase & Co.", AssetCategory.Stock),
        ["V"] = new("Visa Inc.", AssetCategory.Stock),
        ["MA"] = new("Mastercard Inc.", AssetCategory.Stock),
        ["UNH"] = new("UnitedHealth Group Inc.", AssetCategory.Stock),
        ["XOM"] = new("Exxon Mobil Corp.", AssetCategory.Stock),
        ["JNJ"] = new("Johnson & Johnson", AssetCategory.Stock),
        ["WMT"] = new("Walmart Inc.", AssetCategory.Stock),
        ["PG"] = new("Procter & Gamble Co.", AssetCategory.Stock),
        ["HD"] = new("Home Depot Inc.", AssetCategory.Stock),
        ["COST"] = new("Costco Wholesale Corp.", AssetCategory.Stock),
        ["ORCL"] = new("Oracle Corp.", AssetCategory.Stock),
        ["NFLX"] = new("Netflix Inc.", AssetCategory.Stock),
        ["AMD"] = new("Advanced Micro Devices Inc.", AssetCategory.Stock),
        ["CRM"] = new("Salesforce Inc.", AssetCategory.Stock),
        ["ADBE"] = new("Adobe Inc.", AssetCategory.Stock),
        ["INTC"] = new("Intel Corp.", AssetCategory.Stock),
        ["CSCO"] = new("Cisco Systems Inc.", AssetCategory.Stock),
        ["PEP"] = new("PepsiCo Inc.", AssetCategory.Stock),
        ["KO"] = new("Coca-Cola Co.", AssetCategory.Stock),
        ["MCD"] = new("McDonald's Corp.", AssetCategory.Stock),
        ["DIS"] = new("Walt Disney Co.", AssetCategory.Stock),
        ["BA"] = new("Boeing Co.", AssetCategory.Stock),
        ["NKE"] = new("Nike Inc.", AssetCategory.Stock),
        ["PFE"] = new("Pfizer Inc.", AssetCategory.Stock),
        ["BAC"] = new("Bank of America Corp.", AssetCategory.Stock),
        ["WFC"] = new("Wells Fargo & Co.", AssetCategory.Stock),
        ["GS"] = new("Goldman Sachs Group Inc.", AssetCategory.Stock),
        ["MS"] = new("Morgan Stanley", AssetCategory.Stock),
        ["ABBV"] = new("AbbVie Inc.", AssetCategory.Stock),
        ["LLY"] = new("Eli Lilly & Co.", AssetCategory.Stock),
        ["TMO"] = new("Thermo Fisher Scientific Inc.", AssetCategory.Stock),
        ["ACN"] = new("Accenture plc", AssetCategory.Stock),
        ["TXN"] = new("Texas Instruments Inc.", AssetCategory.Stock),
        ["QCOM"] = new("Qualcomm Inc.", AssetCategory.Stock),
        ["INTU"] = new("Intuit Inc.", AssetCategory.Stock),
        ["IBM"] = new("International Business Machines Corp.", AssetCategory.Stock),
        ["GE"] = new("General Electric Co.", AssetCategory.Stock),
        ["CAT"] = new("Caterpillar Inc.", AssetCategory.Stock),
        ["UPS"] = new("United Parcel Service Inc.", AssetCategory.Stock),
        ["RTX"] = new("RTX Corp.", AssetCategory.Stock),
        ["LMT"] = new("Lockheed Martin Corp.", AssetCategory.Stock),
        ["SBUX"] = new("Starbucks Corp.", AssetCategory.Stock),
        ["NOW"] = new("ServiceNow Inc.", AssetCategory.Stock),
        ["UBER"] = new("Uber Technologies Inc.", AssetCategory.Stock),
        ["PYPL"] = new("PayPal Holdings Inc.", AssetCategory.Stock),
        ["SHOP"] = new("Shopify Inc.", AssetCategory.Stock),
        ["PLTR"] = new("Palantir Technologies Inc.", AssetCategory.Stock),
        ["COIN"] = new("Coinbase Global Inc.", AssetCategory.Stock),
        ["SNOW"] = new("Snowflake Inc.", AssetCategory.Stock),

        // ── International stocks (non-USD; native currency inferred from the exchange suffix) ──
        ["HSBA.L"] = new("HSBC Holdings plc", AssetCategory.Stock),       // London (GBP)
        ["BP.L"] = new("BP plc", AssetCategory.Stock),
        ["SHEL.L"] = new("Shell plc", AssetCategory.Stock),
        ["VOD.L"] = new("Vodafone Group plc", AssetCategory.Stock),
        ["AZN.L"] = new("AstraZeneca plc", AssetCategory.Stock),
        ["GSK.L"] = new("GSK plc", AssetCategory.Stock),
        ["ULVR.L"] = new("Unilever plc", AssetCategory.Stock),
        ["RIO.L"] = new("Rio Tinto plc", AssetCategory.Stock),
        ["BARC.L"] = new("Barclays plc", AssetCategory.Stock),
        ["LLOY.L"] = new("Lloyds Banking Group plc", AssetCategory.Stock),
        ["SAP.DE"] = new("SAP SE", AssetCategory.Stock),                  // Frankfurt (EUR)
        ["SIE.DE"] = new("Siemens AG", AssetCategory.Stock),
        ["VOW3.DE"] = new("Volkswagen AG", AssetCategory.Stock),
        ["BMW.DE"] = new("Bayerische Motoren Werke AG", AssetCategory.Stock),
        ["BAS.DE"] = new("BASF SE", AssetCategory.Stock),
        ["ALV.DE"] = new("Allianz SE", AssetCategory.Stock),
        ["MC.PA"] = new("LVMH Moët Hennessy Louis Vuitton SE", AssetCategory.Stock), // Paris (EUR)
        ["OR.PA"] = new("L'Oréal SA", AssetCategory.Stock),
        ["AIR.PA"] = new("Airbus SE", AssetCategory.Stock),
        ["SAN.PA"] = new("Sanofi SA", AssetCategory.Stock),
        ["ASML.AS"] = new("ASML Holding NV", AssetCategory.Stock),        // Amsterdam (EUR)
        ["INGA.AS"] = new("ING Groep NV", AssetCategory.Stock),
        ["NESN.SW"] = new("Nestlé SA", AssetCategory.Stock),             // Zurich (CHF)
        ["ROG.SW"] = new("Roche Holding AG", AssetCategory.Stock),
        ["NOVN.SW"] = new("Novartis AG", AssetCategory.Stock),
        ["7203.T"] = new("Toyota Motor Corp.", AssetCategory.Stock),      // Tokyo (JPY)
        ["6758.T"] = new("Sony Group Corp.", AssetCategory.Stock),
        ["9984.T"] = new("SoftBank Group Corp.", AssetCategory.Stock),
        ["0700.HK"] = new("Tencent Holdings Ltd.", AssetCategory.Stock),  // Hong Kong (HKD)
        ["9988.HK"] = new("Alibaba Group Holding Ltd.", AssetCategory.Stock),
        ["RY.TO"] = new("Royal Bank of Canada", AssetCategory.Stock),     // Toronto (CAD)
        ["TD.TO"] = new("Toronto-Dominion Bank", AssetCategory.Stock),
        ["BHP.AX"] = new("BHP Group Ltd.", AssetCategory.Stock),          // Sydney (AUD)
        ["CBA.AX"] = new("Commonwealth Bank of Australia", AssetCategory.Stock),

        // ── Crypto (USD) ──
        ["BTC"] = new("Bitcoin", AssetCategory.Crypto),
        ["ETH"] = new("Ethereum", AssetCategory.Crypto),
        ["SOL"] = new("Solana", AssetCategory.Crypto),
        ["XRP"] = new("XRP", AssetCategory.Crypto),
        ["ADA"] = new("Cardano", AssetCategory.Crypto),
        ["DOGE"] = new("Dogecoin", AssetCategory.Crypto),
        ["DOT"] = new("Polkadot", AssetCategory.Crypto),
        ["AVAX"] = new("Avalanche", AssetCategory.Crypto),
        ["LINK"] = new("Chainlink", AssetCategory.Crypto),
        ["LTC"] = new("Litecoin", AssetCategory.Crypto),
        ["BCH"] = new("Bitcoin Cash", AssetCategory.Crypto),
        ["MATIC"] = new("Polygon", AssetCategory.Crypto),
        ["UNI"] = new("Uniswap", AssetCategory.Crypto),
        ["ATOM"] = new("Cosmos", AssetCategory.Crypto),
        ["XLM"] = new("Stellar", AssetCategory.Crypto),
        ["TRX"] = new("TRON", AssetCategory.Crypto),
        ["NEAR"] = new("NEAR Protocol", AssetCategory.Crypto),
        ["APT"] = new("Aptos", AssetCategory.Crypto),
        ["ARB"] = new("Arbitrum", AssetCategory.Crypto),
        ["OP"] = new("Optimism", AssetCategory.Crypto),

        // ── FX pairs (native currency = the pair's quote currency) ──
        ["EURUSD"] = new("Euro / US Dollar", AssetCategory.Currency),
        ["GBPUSD"] = new("British Pound / US Dollar", AssetCategory.Currency),
        ["USDJPY"] = new("US Dollar / Japanese Yen", AssetCategory.Currency),
        ["USDCHF"] = new("US Dollar / Swiss Franc", AssetCategory.Currency),
        ["AUDUSD"] = new("Australian Dollar / US Dollar", AssetCategory.Currency),
        ["USDCAD"] = new("US Dollar / Canadian Dollar", AssetCategory.Currency),
        ["NZDUSD"] = new("New Zealand Dollar / US Dollar", AssetCategory.Currency),
        ["EURGBP"] = new("Euro / British Pound", AssetCategory.Currency),
        ["EURJPY"] = new("Euro / Japanese Yen", AssetCategory.Currency),
        ["GBPJPY"] = new("British Pound / Japanese Yen", AssetCategory.Currency),
        ["EURCHF"] = new("Euro / Swiss Franc", AssetCategory.Currency),
        ["USDCNY"] = new("US Dollar / Chinese Yuan", AssetCategory.Currency),
        ["USDHKD"] = new("US Dollar / Hong Kong Dollar", AssetCategory.Currency),
        ["USDSGD"] = new("US Dollar / Singapore Dollar", AssetCategory.Currency),
        ["USDMXN"] = new("US Dollar / Mexican Peso", AssetCategory.Currency),
    };

    // Gated on Demo mode: when off this provider serves nothing (Supports false → the repository's
    // first-match routing skips it for quotes/candles and falls through to the real providers); when on it
    // serves every asset class.
    public bool Supports(AssetCategory category) => MarketSettingsManager.Instance.DemoMode;

    // In Demo mode, be the SOLE active source so the mock wins everywhere — including the search fan-out the
    // repository would otherwise send to live providers too (which Supports() can't gate). Off → not
    // exclusive, so the real providers serve as normal.
    public bool IsExclusive => MarketSettingsManager.Instance.DemoMode;

    public Task<IReadOnlyList<DomainInstrument>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Self-gate on Demo mode: when ON the mock is exclusive, so the repository sends search to it alone;
        // when OFF it's still in the repository's search fan-out (exclusivity doesn't apply), so returning []
        // keeps the catalog from polluting live results.
        query = MarketSettingsManager.Instance.DemoMode ? query?.Trim() ?? string.Empty : string.Empty;
        IReadOnlyList<DomainInstrument> matches = query.Length == 0
            ? []
            // Match on symbol OR name against the curated real-instrument corpus — never fabricate a symbol.
            // Prefix-on-symbol hits rank first (so "AAP" surfaces AAPL ahead of name-only matches), then
            // alphabetical; capped so a broad query ("a") returns a sane page like the live providers do.
            : [.. Catalog
                .Where(kv => kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                          || kv.Value.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(kv => kv.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(25)
                .Select(kv => new DomainInstrument(kv.Key, kv.Value.Name, kv.Value.Category))];
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(
        IReadOnlyList<DomainInstrument> instruments, CancellationToken ct = default)
    {
        IReadOnlyList<DomainQuote> quotes =
        [
            // Seed override (nice hand-tuned price + native currency) for the headline symbols; otherwise
            // SYNTHESIZE a stable, plausible quote for ANY symbol so an off-seed real holding (added while
            // live) still prices in demo mode instead of showing a blank row.
            .. instruments.Select(i => Seed.TryGetValue(i.Symbol, out var s)
                ? new DomainQuote(i.Symbol, i.Name, i.Category, s.Price, s.Change, s.Pct, IsValid: true,
                    Currency: s.Currency)
                : SynthesizeQuote(i)),
        ];
        return Task.FromResult(quotes);
    }

    public Task<DomainCandleSeries> GetCandlesAsync(
        DomainInstrument instrument, ChartRange range, CancellationToken ct = default)
    {
        // A deterministic random walk anchored on the symbol's price (seed override, else the synthesized
        // price, so the chart agrees with the quote) so the chart renders offline / on a free key. Point
        // count varies by range for a believable shape; seeded by (symbol, range) so the same chart is
        // stable across re-fetches.
        var basePrice = Seed.TryGetValue(instrument.Symbol, out var s) && s.Price > 0
            ? s.Price
            : SynthesizeQuote(instrument).Price;
        var count = range switch
        {
            ChartRange.OneDay => 78,
            ChartRange.OneWeek => 65,
            ChartRange.OneMonth => 120,
            ChartRange.OneYear => 252,
            ChartRange.FiveYear => 260,
            _ => 100,
        };

        var rng = new Random(HashCode.Combine(instrument.Symbol, range));
        var step = range.Lookback() / count;
        var now = DateTimeOffset.UtcNow;
        var price = (double)basePrice * 0.92; // start below the seed so the walk trends toward it
        var points = new List<CandlePoint>(count);
        for (var i = 0; i < count; i++)
        {
            price *= 1 + ((rng.NextDouble() - 0.48) * 0.02); // small per-step move, slight upward drift
            points.Add(new CandlePoint(now - (step * (count - i)), (decimal)price));
        }

        IReadOnlyList<CandlePoint> pts = points;
        return Task.FromResult(new DomainCandleSeries(instrument.Symbol, range, pts, IsValid: true));
    }

    // Build a stable, plausible quote for a non-seeded instrument. Native currency is inferred from the
    // symbol shape; price/change are derived from a stable hash of the symbol so they're identical across
    // refreshes AND process restarts (unlike string.GetHashCode, which is per-process randomized).
    private static DomainQuote SynthesizeQuote(DomainInstrument instrument)
    {
        var currency = InferCurrency(instrument.Symbol, instrument.Category);
        var (price, change, pct) = SynthesizePrice(instrument.Symbol, instrument.Category, currency);
        var name = string.IsNullOrWhiteSpace(instrument.Name) ? instrument.Symbol : instrument.Name;
        return new DomainQuote(instrument.Symbol, name, instrument.Category, price, change, pct,
            IsValid: true, Currency: currency);
    }

    // Native currency by category + symbol shape: crypto is quoted in USD; an FX pair's price is in its
    // quote (2nd) currency; a stock's currency comes from its exchange suffix (.L → GBP, .DE → EUR, …),
    // defaulting to USD for a plain ticker.
    private static string InferCurrency(string symbol, AssetCategory category) => category switch
    {
        AssetCategory.Crypto => "USD",
        AssetCategory.Currency => CurrencyHelper.QuoteCurrencyOfPair(symbol),
        _ => ExchangeSuffixCurrency(symbol),
    };

    // Map a stock's exchange suffix (the bit after the last '.') to its trading currency. Covers the
    // exchanges in Catalog plus a few common others; unknown/suffix-less → USD.
    private static string ExchangeSuffixCurrency(string symbol)
    {
        var dot = symbol.LastIndexOf('.');
        if (dot < 0 || dot == symbol.Length - 1)
            return "USD";
        return symbol[(dot + 1)..].ToUpperInvariant() switch
        {
            "L" => "GBP",
            "DE" or "F" or "PA" or "AS" or "MI" or "MC" or "BR" or "LS" or "VI" or "HE" or "IR" => "EUR",
            "SW" => "CHF",
            "T" => "JPY",
            "HK" => "HKD",
            "TO" or "V" => "CAD",
            "AX" => "AUD",
            "NZ" => "NZD",
            "SS" or "SZ" => "CNY",
            "KS" or "KQ" => "KRW",
            "BO" or "NS" => "INR",
            "ST" => "SEK",
            "OL" => "NOK",
            "CO" => "DKK",
            "SA" => "BRL",
            "MX" => "MXN",
            "JO" => "ZAR",
            "TA" => "ILS",
            _ => "USD",
        };
    }

    // Deterministic price + day-change for a symbol. Magnitude is category-appropriate (stock tens-to-low-
    // hundreds, crypto a wide $1–$10k log spread, FX a realistic rate band) and the daily move is ±3%.
    // Two independent hash draws (one for level, one for the move) keep price and change uncorrelated.
    private static (decimal Price, decimal Change, decimal Pct) SynthesizePrice(
        string symbol, AssetCategory category, string currency)
    {
        var level = Unit(symbol);
        var move = Unit(symbol + "|chg");

        decimal price;
        int changeDecimals;
        switch (category)
        {
            case AssetCategory.Crypto:
                price = (decimal)Math.Round(Math.Pow(10, level * 4), 2); // ~$1 .. ~$10,000
                changeDecimals = 2;
                break;
            case AssetCategory.Currency:
                price = currency == "JPY"
                    ? (decimal)Math.Round(90 + (level * 80), 3)    // ~90 .. 170 (yen pairs)
                    : (decimal)Math.Round(0.6 + (level * 1.2), 4); // ~0.60 .. 1.80
                changeDecimals = currency == "JPY" ? 3 : 4;
                break;
            default: // Stock — scaled so non-USD prices read at a believable magnitude (e.g. JPY in thousands)
                price = (decimal)Math.Round((15 + (level * 485)) * StockCurrencyScale(currency), 2);
                changeDecimals = 2;
                break;
        }

        var pct = (decimal)Math.Round((move - 0.5) * 6, 2); // -3.00% .. +3.00%
        var change = Math.Round(price * pct / 100m, changeDecimals);
        return (price, change, pct);
    }

    // Rough order-of-magnitude factor so a synthesized stock price reads plausibly in its native currency
    // (a yen stock in the thousands, a won stock in the tens of thousands). Currencies near USD scale = 1.
    private static double StockCurrencyScale(string currency) => currency switch
    {
        "JPY" => 30,
        "KRW" => 200,
        "HKD" => 8,
        "INR" => 80,
        _ => 1,
    };

    // A deterministic [0,1) fraction from a stable 32-bit FNV-1a hash of the input (24 high-quality bits).
    private static double Unit(string s) => (Fnv1a(s) & 0xFFFFFF) / (double)0x1000000;

    private static uint Fnv1a(string s)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }
}

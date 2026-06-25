using System;
using System.Globalization;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace MarketExtension;

// Extension settings, surfaced in Command Palette's Settings UI and persisted to
// market.settings.json under the CmdPal settings folder. Singleton, modeled on
// reference/settings/AdbSettingsManager.cs. Wired into the host via
// MarketExtensionCommandsProvider (Settings = MarketSettingsManager.Instance.Settings).
//
// Settings: one API key per data provider, plus the price refresh interval (minutes; 0 = off). Keys
// are user-supplied at runtime — there is no built-in/baked key, and an empty key just means that
// provider is skipped. Keys are named per-provider because each provider has its own.
//
// Note: keys are NOT masked — the toolkit's TextSetting has no password mode; settings live in a
// local plaintext JSON file anyway.
//
// ⚠️ Toolkit quirk: TextSetting renders **Description** as the field's on-screen label (it maps Label →
// the Adaptive Card `title`, which Input.Text ignores). So the user-visible text must go in Description;
// Label is kept only as the semantic name.
internal sealed class MarketSettingsManager : JsonSettingsManager
{
    public static readonly MarketSettingsManager Instance = new();

    // Default auto-refresh cadence when the field is blank/unparseable.
    private const int DefaultRefreshMinutes = 5;

    private readonly TextSetting _twelveDataApiKey = new("twelveDataApiKey", string.Empty)
    {
        Label = "Twelve Data API key",
        Description = "Twelve Data API key. Used first when set.",
        Placeholder = "Paste your API key",
    };

    private readonly TextSetting _finnhubApiKey = new("finnhubApiKey", string.Empty)
    {
        Label = "Finnhub API key",
        Description = "Finnhub API key.",
        Placeholder = "Paste your API key",
    };

    private readonly TextSetting _refreshMinutes = new(
        "refreshMinutes", DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture))
    {
        Label = "Price refresh interval (minutes)",
        Description = "Price refresh interval in minutes (0 = off).",
        Placeholder = DefaultRefreshMinutes.ToString(CultureInfo.InvariantCulture),
    };

    private readonly ToggleSetting _showRateLimitErrors = new("showRateLimitErrors", true)
    {
        Label = "Show rate-limit warnings",
        Description = "Show a banner when prices are stale from provider rate-limiting.",
    };

    private readonly ToggleSetting _demoMode = new("demoMode", false)
    {
        Label = "Demo mode",
        Description = "Show built-in sample data instead of live prices. Ddeal for trying out the extension.",
    };

    // The reporting currency for the (upcoming) Portfolio screen: the single currency its totals are shown
    // in, so holdings priced in other currencies can be converted into it (conversion itself is future
    // work — for now this just records the preference). The choice list mirrors the currencies the FX
    // provider (Frankfurter/ECB) can convert between, which is also the set AssetIconResolver maps to
    // flags. The first entry (USD) is the default — matching the app's current single-quote-currency
    // assumption. ChoiceSetSetting renders a dropdown; its stored Value is the selected code (e.g. "USD").
    private readonly ChoiceSetSetting _portfolioCurrency = new("portfolioCurrency",
    [
        new ChoiceSetSetting.Choice("US Dollar (USD)", "USD"),
        new ChoiceSetSetting.Choice("Euro (EUR)", "EUR"),
        new ChoiceSetSetting.Choice("British Pound (GBP)", "GBP"),
        new ChoiceSetSetting.Choice("Japanese Yen (JPY)", "JPY"),
        new ChoiceSetSetting.Choice("Swiss Franc (CHF)", "CHF"),
        new ChoiceSetSetting.Choice("Australian Dollar (AUD)", "AUD"),
        new ChoiceSetSetting.Choice("Canadian Dollar (CAD)", "CAD"),
        new ChoiceSetSetting.Choice("New Zealand Dollar (NZD)", "NZD"),
        new ChoiceSetSetting.Choice("Chinese Yuan (CNY)", "CNY"),
        new ChoiceSetSetting.Choice("Hong Kong Dollar (HKD)", "HKD"),
        new ChoiceSetSetting.Choice("Singapore Dollar (SGD)", "SGD"),
        new ChoiceSetSetting.Choice("Swedish Krona (SEK)", "SEK"),
        new ChoiceSetSetting.Choice("Norwegian Krone (NOK)", "NOK"),
        new ChoiceSetSetting.Choice("Danish Krone (DKK)", "DKK"),
        new ChoiceSetSetting.Choice("Polish Złoty (PLN)", "PLN"),
        new ChoiceSetSetting.Choice("South African Rand (ZAR)", "ZAR"),
        new ChoiceSetSetting.Choice("Mexican Peso (MXN)", "MXN"),
        new ChoiceSetSetting.Choice("Indian Rupee (INR)", "INR"),
        new ChoiceSetSetting.Choice("Brazilian Real (BRL)", "BRL"),
        new ChoiceSetSetting.Choice("South Korean Won (KRW)", "KRW"),
    ])
    {
        Label = "Portfolio currency",
        Description = "Currency for portfolio totals. Other holdings are converted into it.",
    };

    // Observable "is any pricing key configured" flag, driven by SettingsChanged (below). UI surfaces
    // (the missing-key hint) subscribe and re-render the instant the user adds/removes a key, instead of
    // re-querying on each GetItems. Distinct-until-changed means it emits only when the bool actually
    // flips. The providers do NOT use this — they read the key values pull-style on each request.
    private readonly MutableStateFlow<bool> _hasAnyApiKey = new(false);

    // Observable form of the Demo-mode toggle (below), driven by SettingsChanged. Unlike the other settings
    // — read pull-style on the next refresh — flipping demo mode swaps the entire data SOURCE, so every
    // cached price and FX rate the app is holding was produced by the OTHER source and is now wrong. Surfaces
    // subscribe and reset the instant it flips: the priced pages drop their price cache (re-fetching if
    // visible, else on next open), the dock bands re-price, and CurrencyConverter clears its rate cache.
    // Distinct-until-changed → emits only when the toggle actually flips.
    private readonly MutableStateFlow<bool> _demoModeFlow = new(false);

    // The key the Twelve Data provider should use — the user's setting, or empty when unset. Read as a
    // property (not cached) so a key change applies on the next request without a reload.
    public string TwelveDataApiKey => _twelveDataApiKey.Value?.Trim() ?? string.Empty;

    // Whether a Twelve Data key has been configured. The provider's Supports() returns false when this
    // is false, so the repository's first-match routing falls through to Finnhub/Frankfurter.
    public bool HasTwelveDataApiKey => TwelveDataApiKey.Length > 0;

    // The key the Finnhub provider should use — the user's setting, or empty when unset (there is no
    // built-in fallback). Read as a property (not cached) so a key change in settings applies on the
    // next request without a reload.
    public string FinnhubApiKey => _finnhubApiKey.Value?.Trim() ?? string.Empty;

    // Whether a Finnhub key has been configured. The provider short-circuits to "no data" when false
    // rather than firing keyless requests that would just 401.
    public bool HasFinnhubApiKey => FinnhubApiKey.Length > 0;

    // Observable form of "is at least one pricing-provider key set" (Twelve Data OR Finnhub). Frankfurter
    // FX is keyless, but stocks/crypto need a key — so this drives the missing-key hint. Read .Value for a
    // snapshot, or Subscribe to re-render when it flips. Updated by the SettingsChanged handler in the ctor.
    public StateFlow<bool> HasAnyApiKey => _hasAnyApiKey;

    // Auto-refresh cadence in minutes; 0 means off. Bad/negative input falls back to the default.
    public int RefreshMinutes =>
        int.TryParse(_refreshMinutes.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v
            : DefaultRefreshMinutes;

    // Whether the poll loop should run at all (false when the user picked 0 = off).
    public bool AutoRefreshEnabled => RefreshMinutes > 0;

    // The cadence as a TimeSpan for the (upcoming) PeriodicTimer-driven poll loop.
    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(RefreshMinutes);

    // Whether the rate-limited banner (RateLimitHint) shows while a provider is throttling requests.
    // Default on; the user can hide it in Settings if they'd rather not see it. Read pull-style each render,
    // so a toggle applies the next time a priced page re-renders (e.g. on navigating back to it).
    public bool ShowRateLimitErrors => _showRateLimitErrors.Value;

    // When on, the app serves built-in sample data (MockMarketDataProvider + a static FX table in
    // CurrencyConverter) instead of calling any live market-data or FX API — no key or connectivity needed.
    // The VALUE is read pull-style per request (MockMarketDataProvider.Supports/SearchAsync and
    // CurrencyConverter check it each call), but flipping the toggle applies IMMEDIATELY: the SettingsChanged
    // handler publishes DemoModeChanged (below), and every surface subscribes to drop its cached prices/rates
    // and re-fetch through the now-current routing — no reload, no waiting for the next refresh. Default off
    // → ships live.
    public bool DemoMode => _demoMode.Value;

    // Observable form of DemoMode: subscribe to reset cached data the instant the toggle flips (see the
    // _demoModeFlow field note for what each surface does). Read .Value for a snapshot. Updated by the
    // SettingsChanged handler in the ctor.
    public StateFlow<bool> DemoModeChanged => _demoModeFlow;

    // The reporting currency the Portfolio screen rolls up into (ISO 4217 code, e.g. "USD"). Read
    // pull-style so a change applies the next time the portfolio re-prices, no reload needed. Falls back
    // to USD if somehow unset. Conversion of foreign-currency holdings into this is future work.
    public string PortfolioCurrency =>
        string.IsNullOrWhiteSpace(_portfolioCurrency.Value) ? "USD" : _portfolioCurrency.Value;

    private MarketSettingsManager()
    {
        FilePath = Path.Combine(Utilities.BaseSettingsPath("Microsoft.CmdPal"), "market.settings.json");
        Settings.Add(_twelveDataApiKey);
        Settings.Add(_finnhubApiKey);
        Settings.Add(_refreshMinutes);
        Settings.Add(_showRateLimitErrors);
        Settings.Add(_demoMode);
        Settings.Add(_portfolioCurrency);
        LoadSettings();
        _hasAnyApiKey.Update(HasTwelveDataApiKey || HasFinnhubApiKey); // seed from persisted keys (no subscribers yet)
        _demoModeFlow.Update(DemoMode); // seed from the persisted toggle (no subscribers yet)
        Settings.SettingsChanged += (_, _) =>
        {
            SaveSettings();
            // Publish the key-presence flag so the missing-key hint reacts immediately. Distinct-until-
            // changed swallows this unless a key was just added or cleared.
            _hasAnyApiKey.Update(HasTwelveDataApiKey || HasFinnhubApiKey);
            // Publish the demo-mode flag so every surface resets the instant it flips (the cached prices +
            // FX rates came from the other data source). Distinct-until-changed → only on a real flip.
            _demoModeFlow.Update(DemoMode);
        };
    }
}

using System;
using System.Globalization;
using System.Resources;

namespace MarketExtension;

// Central accessor for the app's user-facing text. English values live in Properties/Resources.resx
// (the neutral culture, embedded in the main assembly as "MarketExtension.Properties.Resources").
//
// At runtime ResourceManager resolves the right culture from CultureInfo.CurrentUICulture (the Windows
// display language) via satellite assemblies. Shipping a new language is purely additive: drop a
// Resources.<culture>.resx next to the neutral one with the same keys translated — the SDK compiles it
// into a <culture>/MarketExtension.resources.dll satellite, and lookups fall back to English for any key
// a translation is missing. No code here changes.
//
// FAIL LOUD by design: these lookups DON'T swallow errors. A missing resource set (packaging/embed broken
// → MissingManifestResourceException), a missing key (a typo or a forgotten resx entry → thrown below), or a
// malformed format template (a bad placeholder, most likely a future translation → FormatException) are all
// real bugs we want to surface immediately, not paper over with a degraded UI that might ship. Mirrors the
// pattern CmdPal's own built-in extensions use (Properties.Resources.ResourceManager + a throwing GetString).
internal static class Strings
{
    private static readonly ResourceManager Manager =
        new("MarketExtension.Properties.Resources", typeof(Strings).Assembly);

    // The localized string for the current UI culture. Throws if the key isn't in the resx (or the resource
    // set can't be loaded at all) — a missing string is a bug, so crash rather than render a key name.
    public static string Get(string key) =>
        Manager.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new InvalidOperationException($"Missing resource string '{key}'");

    // A localized format template filled with the given args. The template ("Added {0} to watchlist") is
    // localized; the args are already-formatted strings, so culture here is moot — the deliberate
    // CultureInfo.Invariant number/currency formatting still happens in the Ui layer that produces them.
    // A malformed template throws (FormatException) — we want to know about a bad placeholder, not hide it.
    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

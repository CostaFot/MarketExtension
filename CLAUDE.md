# Markets Extension for Command Palette — Claude Guide

A PowerToys **Command Palette** extension showing live market data — stocks, crypto, forex: search,
watchlist, favorites, portfolio, news, per-symbol detail charts, and pinnable Dock bands. .NET 9 / C# /
MSIX. Hobby project: ships **self-contained single-file JIT** (no trim/AOT — see `notes/providers.md`).

## Where things are documented

This file is the quick orientation — keep it short. Deep/historical detail lives in `notes/`:

| File | Contents |
|---|---|
| `notes/architecture.md` | The market-data architecture: layering + naming, the full file map, the shared quote/news caches, repository orchestration, the `ObserveOn` deadlock fix, polling. |
| `notes/features.md` | Feature deep-dives: navigation/UX, Portfolio, multi-currency, Demo mode, the symbol-detail chart, asset logos, the News feed, Settings. |
| `notes/providers.md` | Twelve Data / Finnhub / Frankfurter API specifics, how to add a provider, the AOT-off rationale + the JSON source-gen gotcha. |
| `notes/cmdpal-toolkit.md` | CmdPal toolkit patterns: the page-activation hook, the ContentPage focus gotcha, CommandResult / ListItem / Icon / ProcessHelper reference. |
| `notes/releasing.md` | Version bump → GitHub Actions → Partner Center, local build/deploy, one-time setup TODOs, localization. |
| `notes/changelog.md` | Condensed "what shipped each round" log + open/deferred items. |
| `notes/CLAUDE_copy.md` | The full prior (very long) version of this guide — verbatim archive. |
| `reference/` | Real implementations from the AdbExtension this was scaffolded from — **not compiled**. Copy/adapt rather than reinventing; see `reference/README.md`. |

## Documentation links

- [Extension overview & concepts](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extension-development)
- [Creating an extension](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/creating-an-extension)
- [Toolkit namespace — full class list](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/microsoft-commandpalette-extensions-toolkit)
- [Command results](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/command-results)
- [Adding Dock support](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support) — full writeup in `reference/dock-support.md`

## Build & Deploy

`dotnet build MarketExtension.sln` (or Visual Studio). Deploy the MSIX package, then reload Command Palette
to pick up changes. Full release flow + caveats (ARM64 RID pin, `-p:Platform=x64`) → `notes/releasing.md`.

## Project conventions

- New commands → `MarketExtension/Commands/`, extend `InvokableCommand`.
- New pages → `MarketExtension/Pages/`, extend `ListPage`, `DynamicListPage`, or `ContentPage`.
- All external process execution goes through `ProcessHelper` (`Run()` for captured CLI; `OpenUrl()` to
  launch a URL/file) — **never** use `Process` directly in command files.
- **Error toasts** use `CommandResult.KeepOpen()` so the user can read them; one-shot success toasts use the
  default `Dismiss()`. Exception: in-place list mutations (watchlist/favorites add/remove — see
  `Commands/MembershipCommands.cs`) toast **and** `KeepOpen()`.
- **Don't hardcode user-facing strings** — add a key to `Properties/Resources.resx` and use
  `Strings.Get`/`Strings.Format`. Regenerate `Resources.Designer.cs` from VS after editing the resx.
- **Icons:** instrument rows / dock buttons / detail header use `AssetIconResolver.Resolve(...)` (real asset
  logo, Segoe-glyph fallback); page chrome + the extension entry use
  `IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png")`.

## Architecture in one screen

Layered, provider-agnostic. **Keep the naming for new code:**

- `Api*` — raw provider DTOs. `Domain*` — provider-agnostic, no formatting. `*Entity` — data-layer storage
  models that never escape a data source. `Ui*` — the ONLY place formatting/SVG lives. Enums
  (`AssetCategory`, `ChartRange`, `NewsCategory`, …) stay unprefixed.
- `Data/MarketRepository.cs` is **the coordinator the UI depends on**: routes each instrument to the first
  `IMarketDataProvider` that `Supports` its category, owns the shared observable quote cache + the single
  poll loop, and exposes `ObserveQuotes(...)` / `GetCandlesAsync(...)` / `ObserveNews(...)`.
- **All priced surfaces OBSERVE the cache** (they don't fetch) so the same symbol can't drift between two
  surfaces. The lone exception is the symbol-detail chart (a separate candle path).
- **Entry point** (`MarketExtensionCommandsProvider`): one top-level **Markets** command →
  `MarketsPage` hub (Search / Watchlist / Favorites / Portfolio / News / Data Sources / Settings). Three
  **dock bands**: Favorites, Portfolio, News. Providers registered Mock → TwelveData → Finnhub → Frankfurter.

Full file map, the cache/orchestration design, and the load-bearing `ObserveOn` deadlock fix are in
`notes/architecture.md`. To add a data provider, see `notes/providers.md`.

## API keys (runtime only — no build-time key)

Keys are provided **exclusively at runtime** via the extension's settings
(`Settings/MarketSettingsManager.cs` → `…/Microsoft.CmdPal/market.settings.json`); there is **no baked-in
key**. Keys are **per provider** (`TwelveDataApiKey`, `FinnhubApiKey`) because future providers each get
their own. Twelve Data, when its key is set, is primary (stocks/crypto/FX + free charts) and falls back to
Finnhub/Frankfurter when unset. Providers read the key per request (a change applies without a reload) and
short-circuit to "no data" when unset. Frankfurter (FX) is keyless.

## Logging

`Log.Info/Warn/Error(tag, message)` (`Helpers/Log.cs`), tagged by component. **All `[Conditional("DEBUG")]`**
→ compiled out of Release; the MSIX ships silent with no telemetry. Watch Debug builds via VS Debug → Attach
to `MarketExtension.exe` (Managed) or Sysinternals DebugView. **NEVER log the API token.**

## Gotchas (read before editing)

- ⚠️ **The Write tool drops Segoe MDL2 glyph chars** (private-use code points save as blank → blank icon).
  Use a C# Unicode escape (`""`) or build from the code point (`((char)0xE9F9)`), and byte-check after
  editing. (Glyphs already written as `\uXXXX` escapes are safe.)
- ⚠️ **ContentPage with a single `FormContent`** → the host auto-focuses the card's first action, stealing
  Enter (and trapping Ctrl+Enter). No host API suppresses it. Details + the symbol-detail chart's open bug:
  `notes/cmdpal-toolkit.md`.
- ⚠️ **Page-activation hook:** `GetItems()` is called before `ItemsChanged` is subscribed, so a constructor
  `RaiseItemsChanged` is lost. Re-implement `INotifyItemsChanged` and refresh from the `add` accessor — the
  recurring pattern is in `notes/cmdpal-toolkit.md`.
- ⚠️ **The `ObserveOn(TaskPoolScheduler)` seam in `MarketRepository.ObserveQuotes`/`ObserveNews` is
  load-bearing** — removing it (or "fixing" it with `SubscribeOn`) re-creates a gate↔STA deadlock that hangs
  CmdPal. Observe handlers run on a pool thread; don't add your own `Task.Run`/`SubscribeOn`. See
  `notes/architecture.md`.
- ⚠️ **JSON source-gen:** keep all `[JsonSerializable]` for one context on a single `partial` declaration —
  splitting them silently breaks the generator for the whole build. See `notes/providers.md`.
- **AOT/trim is intentionally OFF** — reflection-based code is fine; no IL2026/IL3050 enforcement.
- **Fail loud:** crash on genuinely-wrong states; don't swallow errors and silently degrade.

## Git

- Never amend commits (`git commit --amend`) — always create a new commit, unless explicitly asked to amend
  in the moment.

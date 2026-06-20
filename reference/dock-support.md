# Dock Support — reference for the future ticker phase

Notes for building the **Command Palette Dock band** that shows a live market ticker (the
phase-3 goal). Gathered from the official docs plus two working extensions on this machine.
Nothing here is wired up yet — this is the map for when we start.

## Source material (read these when implementing)

| Source | Location | Why it matters |
|---|---|---|
| Official how-to | https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support | The canonical API contract: `GetDockBands()`, `WrappedDockItem`, band rendering rules |
| **Performance Monitor** (built-in) | `C:\Users\jarla\code\PowerToys\src\modules\cmdpal\ext\Microsoft.CmdPal.Ext.PerformanceMonitor\` | **Closest analog to the ticker.** Exposes *multiple* live-updating bands (one per metric). See `PerformanceMonitorCommandsProvider.cs`, `PerformanceWidgetsPage.cs`, `OnLoadStaticPage.cs` |
| MediaControlsExtension | `C:\Users\jarla\code\MediaControlsExtension\src\MediaControlsExtension\` | A single live band that updates on external events. See `MediaCommandsExtensionCommandsProvider.cs`, `Pages/DockHeadItem.cs` |

## Requirements

- **SDK 0.9 or later** (`Microsoft.CommandPalette.Extensions` >= `0.9.260303001`).
  MarketExtension already references this version, so no upgrade is needed.
- Every `ICommandItem` returned from `GetDockBands()` **must** have a `Command` with a
  **non-empty `Id`**. Items without an Id are silently ignored.

## API surface

Dock support is two optional provider interfaces; the toolkit `CommandProvider` base lets you
just `override` them (this is what our `MarketExtensionCommandsProvider` already extends):

- **`ICommandProvider3.GetDockBands()`** → `ICommandItem[]?` — return one `ICommandItem` per
  **atomic band**. This is the main entry point.
- **`ICommandProvider4.GetCommandItem(string id)`** → `ICommandItem?` — only needed if we want
  users to pin *nested* commands (resolve a command by its Id). Optional; skip for v1.

How each band renders depends on its `Command`:

| Command type | Dock rendering |
|---|---|
| `IInvokableCommand` | one button that runs the command |
| `IListPage` | every item on the page becomes its own button **within one band** |
| `IContentPage` | one expandable button with a flyout |

### `WrappedDockItem` helper (toolkit)

Wraps items into a band backed by a `ListPage`:

```csharp
// multiple buttons in one band
var band = new WrappedDockItem([listItem1, listItem2], "com.costafotiadis.market.favorites", "Markets");
// or a single command:
var band = new WrappedDockItem(command, "Markets");
return [band];
```

## Live-updating bands — the key pattern

This is the mechanism the ticker needs: a band that polls/refreshes and pushes new values to
the Dock while it is visible, and stops when it is not.

The trick (see `OnLoadStaticPage.cs` → `OnLoadBasePage`): **CmdPal subscribes to the page's
`ItemsChanged` event when the band becomes visible and unsubscribes when it is hidden.** So the
`add`/`remove` accessors of that event are de-facto `Loaded()`/`Unloaded()` lifecycle hooks.
This is the same idea as our existing `INotifyItemsChanged` on-load refresh in CLAUDE.md /
`Pages/MarketsPage.cs`, but generalized to also catch *unload* so you can stop background work.

Update loop, as done in `PerformanceWidgetsPage`:
1. `Loaded()` → start the data source (a timer / async poll).
2. On each tick, mutate the band's `ListItem.Title` / `Subtitle` / `Icon` in place.
3. Call `RaiseItemsChanged()` to push the new values to the Dock.
4. `Unloaded()` → stop the timer (Performance Monitor uses a `PushActivate`/`PopActivate`
   refcount because both the main list page and the band can keep the source alive at once).

`DockHeadItem.cs` shows the event-driven variant: instead of a timer it subscribes to a
service's `CurrentMediaSourceChanged` event and throttles updates (`ThrottledAction(150ms)`)
before mutating Title/Subtitle/Icon.

## Sketch for the market ticker

Mirror Performance Monitor's multi-band shape:

- `MarketExtensionCommandsProvider` overrides `GetDockBands()`:
  - One **"Markets" overview band** = a band page that lists each favorited `Quote` as a button
    (Title `"AAPL ▲ +1.2%"`), green/red via Icon or text.
  - Optionally one **band per favorited symbol** (like Perf Monitor's per-metric bands), each
    with a stable Id `com.costafotiadis.market.band.<SYMBOL>`.
- Back each band with an `OnLoad`-style page (port `OnLoadStaticPage.cs`, or extend our existing
  `INotifyItemsChanged` pattern to also stop on unload). On `Loaded()` start polling
  `IMarketDataProvider`; on tick update titles + `RaiseItemsChanged()`; on `Unloaded()` stop.
- Reuse `FavoritesStore` to decide which symbols get bands, and the existing `Quote` model +
  `FormatChange()` for the ▲/▼ display.
- This is also when the data layer should move from `MockMarketDataProvider` to a real, polling
  API implementation (a band that ticks needs live numbers).

## Gotchas

- Forgetting a non-empty `Command.Id` → the band silently disappears.
- Not stopping work on `Unloaded()` → the poller keeps running (and burning CPU/network) after
  the Dock hides the band.
- Bands and the main page can be active simultaneously → guard the shared data source with a
  refcount (Perf Monitor's `PushActivate`/`PopActivate`) rather than a simple bool.

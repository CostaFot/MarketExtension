# Reference library

Real-world implementations from **AdbExtension**, the extension this template was derived
from. These files are kept **verbatim** and are **not compiled** (they live outside the
`MarketExtension/` project folder, so the SDK never globs them into the build, and they
never ship in the MSIX).

Use them as worked examples: find the pattern you need, then copy and adapt it into
`MarketExtension/`. The compiled starters in the project (`SampleCommand`, `SampleListPage`,
`ProcessHelper`) are minimal, build-verified versions of the most common patterns.

## Pages

| File | Demonstrates |
|---|---|
| `pages/AdbExtensionPage.cs` | `DynamicListPage` + async data load + the **`INotifyItemsChanged` on-load refresh** (the #1 gotcha), search filtering, `Section` grouping, a nested `InvokableCommand` |
| `pages/PackageActionsPage.cs` | `ListPage` (synchronous `GetItems()`) + the `INotifyItemsChanged` **fire-on-subscribe** variant; per-item action menu; favorites ordering |
| `pages/InstallApksPage.cs` | file-picker / multi-item action flow |
| `pages/LaunchDeepLinkPage.cs`, `pages/OpenDeepLinkPage.cs` | text-input-driven pages (act on `SearchText`) |

## Commands

| File | Demonstrates |
|---|---|
| `commands/TakeScreenshotCommand.cs` | external process exec, pull a file to disk, success/error toasts, `Win32` errno-2 "tool not found" handling |
| `commands/Toggle*Command.cs` | simple state-toggle commands |
| `commands/Launch/Restart/Kill/ForceStop/ClearAppData/Uninstall*Command.cs` | per-target action commands |
| `commands/Grant/RevokeAllPermissionsCommand.cs` | enumerate sub-items then loop an action over each |
| `commands/Launch/OpenDeepLinkCommand.cs` | pass user input through to a process |
| `commands/ToggleFavoriteCommand.cs` | mutate persisted state then refresh the page |

## Helpers & settings

| File | Demonstrates |
|---|---|
| `helpers/AdbHelper.cs` | `RunAdb` (read **both** streams before `WaitForExit` to avoid deadlocks) + output parsing into records |
| `settings/AdbSettingsManager.cs` | `JsonSettingsManager` singleton with `ToggleSetting`/`TextSetting`, plus a `SuccessToast` helper that honors a "keep palette open" setting |
| `settings/FavoritesStore.cs` | JSON persistence under the Command Palette settings path |
| `AdbExtensionCommandsProvider.cs` | wiring many top-level commands in one provider |

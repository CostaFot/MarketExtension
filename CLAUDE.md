# Market Extension for Command Palette — Claude Guide

## Documentation

- [Extension overview & concepts](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extensions-overview)
- [Creating an extension (getting started guide)](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/creating-an-extension)
- [Toolkit namespace — full class list](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/microsoft-commandpalette-extensions-toolkit/microsoft-commandpalette-extensions-toolkit)
- [Command results](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/command-results)
- [Sample extensions](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/samples)
- [Adding Dock support](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/adding-dock-support) — for the future ticker phase; full writeup + worked examples in [`reference/dock-support.md`](reference/dock-support.md)

## Build & Deploy

Build via Visual Studio or:
```
dotnet build MarketExtension.sln
```
Deploy the MSIX package, then reload Command Palette to pick up changes.

> Debug builds are clean. A **Release** build on an ARM64 box needs a pinned RID, e.g.
> `dotnet build MarketExtension/MarketExtension.csproj -c Release -r win-arm64` (the release
> pipeline pins RIDs already, so this only matters for local Release builds).

## Reference Library (`reference/`)

Real-world implementations from the **AdbExtension** this project was scaffolded from live
in `reference/` — **not compiled** (outside the project folder). Consult them before
writing a new command or page; copy and adapt rather than reinventing. The live code
(`Pages/MarketsPage.cs`, `Pages/FavoritesDockPage.cs`, `Helpers/ProcessHelper.cs`) shows these
patterns in use. See `reference/README.md` for the full index;
the highest-value examples:

| Example | Demonstrates |
|---|---|
| `reference/pages/AdbExtensionPage.cs` | `DynamicListPage` + async load + the `INotifyItemsChanged` on-load refresh, sections, nested command |
| `reference/pages/PackageActionsPage.cs` | `ListPage` (sync `GetItems`) + `INotifyItemsChanged` fire-on-subscribe variant, per-item action menu |
| `reference/commands/TakeScreenshotCommand.cs` | external process exec, pull a file, success/error toasts, `Win32` errno-2 handling |
| `reference/settings/AdbSettingsManager.cs` | `JsonSettingsManager` singleton + a "keep open" success-toast helper |
| `reference/dock-support.md` | **Dock band** how-to for the future ticker: `GetDockBands()`, live-update lifecycle, and pointers to the Performance Monitor + MediaControls extensions on disk |

## Project Conventions

- New commands → `MarketExtension/Commands/`, extend `InvokableCommand`
- New pages → `MarketExtension/Pages/`, extend `ListPage` or `DynamicListPage`
- All external process execution goes through `ProcessHelper.Run()` — never use `Process` directly in command files
- Error toasts use `CommandResult.KeepOpen()` so the user can read them; success toasts use the default `Dismiss()`
- Icons: `new IconInfo("https://github.com/favicon.ico")` per project preference

## Market Data Architecture (the core of this app)

Live quotes flow through three explicitly-named layers + a coordinator. **Keep this layering and
naming for new code.**

```
ApiFinnhubQuoteDto ─(provider maps)→ DomainQuote ─(repository routes+merges)→ IReadOnlyList<DomainQuote>
                                                          └─(page maps via UiQuote.From)→ UiQuote (rendered)
```

**Naming convention:**
- `Api*` — raw provider DTOs (provider-specific), e.g. `ApiFinnhubQuoteDto`.
- `Domain*` — provider-agnostic, **no formatting**; what every provider AND the repository return (`DomainQuote`, `DomainInstrument`).
- `Ui*` — presentation; the ONLY place `FormatPrice()`/`FormatChange()` live (`UiQuote`).
- `AssetCategory` (enum) stays **unprefixed** — shared vocabulary across all layers.

**File map:**

| File | Role |
|---|---|
| `Data/MarketRepository.cs` | **The coordinator the UI depends on.** Routes each `DomainInstrument` to the first `IMarketDataProvider` whose `Supports(AssetCategory)` matches, fans out concurrently, merges into one order-preserving list. No provider for a category → `IsValid:false` placeholder. |
| `Data/IMarketDataProvider.cs` | ONE data source: `bool Supports(AssetCategory)` + `Task<IReadOnlyList<DomainQuote>> GetQuotesAsync(IReadOnlyList<DomainInstrument>, ct)`. |
| `Data/Finnhub/FinnhubMarketDataProvider.cs` | Active provider (`Supports` Stock+Crypto). Maps `ApiFinnhubQuoteDto` → `DomainQuote`. |
| `Data/MockMarketDataProvider.cs` | Offline fallback (`Supports` all). |
| `Data/InstrumentCatalog.cs` | Static `DomainInstrument` list of what to price. |
| `Data/Finnhub/ApiFinnhubQuoteDto.cs` | Raw `/quote` DTO + `FinnhubJsonContext` (source-gen JSON). |
| `Models/{AssetCategory,DomainInstrument,DomainQuote,UiQuote}.cs` | the model layers |

**To add a provider** (e.g. forex): implement `IMarketDataProvider`, declare `Supports`, map its
`Api*` DTO → `DomainQuote`, and register it in `MarketExtensionCommandsProvider`:
`new MarketRepository(new FinnhubMarketDataProvider(), new YourProvider())`. **Zero UI changes.**

**Finnhub specifics:**
- Base `https://finnhub.io/api/v1`, endpoint `/quote`. Free tier ~60 calls/min, ~300/day.
- **No forex on the free tier** — `OANDA:*` returns 403. FX is omitted from `InstrumentCatalog`;
  re-add via a paid plan or a keyless FX provider (e.g. Frankfurter) behind the same seam.
- Symbol formats: stock = bare (`AAPL`), crypto = `BINANCE:{SYM}USDT`, FX = `OANDA:{BASE}_{QUOTE}`.
- An all-zero `/quote` response = invalid/unknown symbol → map to `IsValid:false`.

**AOT/trim:** project is AOT/trim-enabled and treats trim warnings as errors **on publish**, so all
JSON must go through a source-gen `JsonSerializerContext` (never reflection-based `JsonSerializer`).
⚠️ `Settings/FavoritesStore.cs` still uses reflection JSON (IL2026/IL3050) — fine in Debug, will
ERROR on a trimmed Release publish; convert it before shipping a trimmed build.

## API Key (secrets.props — the .NET "local.properties")

- The Finnhub key lives in **gitignored** `MarketExtension/secrets.props`. Fresh clone: copy
  `MarketExtension/secrets.props.template` → `secrets.props` and paste the key.
- The csproj `GenerateSecrets` target bakes it into a generated `Secrets.FinnhubApiKey` const (the
  BuildConfig analog); `FinnhubMarketDataProvider` reads that. Missing file → empty key, build still succeeds.
- Caveat: the key is compiled into the shipped MSIX (extractable). True per-user secrecy = the
  deferred bring-your-own-key in Command Palette settings.

## Logging

- `Log.Info/Warn/Error(tag, message)` (`Helpers/Log.cs`) — tagged by component (`Finnhub`,
  `Repository`, `ComServer`, `Startup`).
- `Info`/`Warn` are `[Conditional("DEBUG")]` → **compiled out of Release** (the MSIX ships silent).
  `Error` also reports to **Sentry**, which survives into Release (no-op until a DSN is set in `Program.cs`).
- Watch live (Debug builds): VS **Debug → Attach to Process → `MarketExtension.exe`** (Managed) →
  Output window; or Sysinternals **DebugView** ("Capture Global Win32"). **NEVER log the API token.**

## Current Status / Next Steps

- **Done:** layered data architecture, live Finnhub provider, `MarketRepository` coordinator,
  `secrets.props` key handling, tagged logging. ⚠️ Working tree is **uncommitted** (on `master`).
- **Deferred / next:** FX provider (keyless Frankfurter); Finnhub `/search` add flow + `PinnedItem`
  persistence; settings UI for bring-your-own-key; dock live-polling timer; rate-limit (429) + error
  UX; convert `FavoritesStore` to source-gen JSON.

## CommandPalette Toolkit — Quick Reference

### Navigation

`Page` extends `Command` (implements `ICommand`). Pass a page anywhere a command is accepted — the palette navigates into it automatically. No string-based registration needed.

```csharp
// Top-level entry point
new CommandItem(new MyPage()) { Title = "My Command" }

// List item that navigates into a sub-page
new ListItem(new MySubPage(arg)) { Title = "Go deeper" }

// List item that runs a command
new ListItem(new MyInvokableCommand()) { Title = "Do thing" }
```

### Base Classes

| Class | Use when |
|---|---|
| `InvokableCommand` | Action with no UI — runs and returns a `CommandResult` |
| `ListPage` | Static list of items |
| `DynamicListPage` | List that reacts to search input — override `UpdateSearchText()` |
| `CommandProvider` | Extension entry point — returns top-level `ICommandItem[]` |

### Page Activation Hook (On-Load Refresh)

The framework calls `FetchItems` → `GetItems()` **before** subscribing to `ItemsChanged`. This means any `RaiseItemsChanged` fired from the constructor is lost and the page shows empty on first open.

The fix: re-implement `INotifyItemsChanged` on the page class and intercept the `add` accessor. The framework subscribes via `INotifyItemsChanged.ItemsChanged +=`, so our accessor fires right after subscription — triggering a second `FetchItems` while the framework is already listening.

**Critical:** use `INotifyItemsChanged.ItemsChanged`, not `IListPage.ItemsChanged` — `ItemsChanged` lives on `INotifyItemsChanged`, and the class must re-list the interface to override base class dispatch.

```csharp
// For pages that load data async (see Pages/MarketsPage.cs, reference/pages/AdbExtensionPage.cs):
internal sealed partial class MyPage : DynamicListPage, INotifyItemsChanged
{
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshData(); } // called every time user navigates to this page
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
}

// For pages with synchronous GetItems() (see reference/pages/PackageActionsPage.cs):
internal sealed partial class MyPage : ListPage, INotifyItemsChanged
{
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(-1)); } // fire immediately after subscribe
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
}
```

Do **not** call `IsLoading = true` + `Task.Run(Load)` from the constructor — the event fires before the framework subscribes and the signal is lost.

### DynamicListPage Pattern

```csharp
internal sealed partial class MyPage : DynamicListPage
{
    public MyPage()
    {
        PlaceholderText = "Type to filter...";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0); // triggers GetItems() re-call

    public override IListItem[] GetItems()
    {
        // Filter using this.SearchText
    }
}
```

### CommandResult Options

```csharp
CommandResult.ShowToast("message")              // show toast, dismiss palette
CommandResult.ShowToast(new ToastArgs {
    Message = "message",
    Result = CommandResult.KeepOpen()           // show toast, keep palette open
})
CommandResult.KeepOpen()                        // do nothing, stay on current page
CommandResult.Dismiss()                         // close the palette
CommandResult.GoBack()                          // navigate to previous page
CommandResult.GoHome()                          // navigate to root
CommandResult.Confirm(new ConfirmationArgs())   // show confirmation dialog
```

### ListItem Properties

```csharp
new ListItem(command)
{
    Title = "Required",
    Subtitle = "Optional secondary text",
    Icon = new IconInfo("https://...") ,        // URL icon
    Section = "Section header",                 // groups items under a header
}
```

### Icon Options

```csharp
new IconInfo("https://example.com/icon.png")   // URL (light+dark same)
new IconInfo(lightIconData, darkIconData)       // separate light/dark
IconHelpers.FromRelativePath("Assets\\foo.png") // bundled asset
```

### InvokableCommand Pattern

```csharp
internal sealed partial class MyCommand : InvokableCommand
{
    public MyCommand()
    {
        Name = "Do Thing";
        Icon = new IconInfo("https://...");
    }

    public override ICommandResult Invoke()
    {
        try
        {
            // do work
            return CommandResult.ShowToast("Done");
        }
        catch (Exception ex) when (ex is Win32Exception w && w.NativeErrorCode == 2)
        {
            return ErrorToast("Required tool not found. Make sure it is on your PATH.");
        }
        catch (Exception ex)
        {
            return ErrorToast($"Unexpected error: {ex.Message}");
        }
    }

    private static ICommandResult ErrorToast(string message) =>
        CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
}
```

## ProcessHelper API

```csharp
// Run any external process. Reads BOTH stdout and stderr before WaitForExit (prevents
// pipe deadlocks). stderr is non-empty only on failure (non-zero exit).
ProcessHelper.Run(string fileName, string arguments, out string stdout, out string stderr);
```

For richer process handling (output parsing into records, etc.) see `reference/helpers/AdbHelper.cs`.

## Releasing a New Version

### Step 1 — Bump version in all 5 files

| File | Field |
|---|---|
| `MarketExtension/MarketExtension.csproj` | `<AppxPackageVersion>` |
| `MarketExtension/Package.appxmanifest` | `Identity Version=` |
| `MarketExtension/app.manifest` | `assemblyIdentity version=` |
| `MarketExtension/build-exe.ps1` | `$Version` default param |
| `MarketExtension/setup-template.iss` | `#define AppVersion` |

One-liner (replace `OLD` and `NEW`):
```powershell
$files = @("MarketExtension/MarketExtension.csproj","MarketExtension/Package.appxmanifest","MarketExtension/app.manifest","MarketExtension/build-exe.ps1","MarketExtension/setup-template.iss")
$files | ForEach-Object { (Get-Content $_) -replace 'OLD','NEW' | Set-Content $_ }
```

### Step 2 — Trigger the GitHub Actions build

```
gh workflow run release-msix.yml --ref master -f release_notes="Your release notes here"
```

This reads the version from the csproj automatically, builds x64 + ARM64, bundles into a `.msixbundle`, signs it, and creates a GitHub Release. (`release-extension.yml` is the alternative self-signed `.exe` installer path.)

### Step 3 — Submit to Partner Center

1. Download the `.msixbundle` from the GitHub Release
2. Partner Center → app → new submission → Packages → upload the `.msixbundle`
3. Update description/notes, submit

## One-time setup (this extension's own identity)

This was scaffolded from AdbExtension. Already changed: COM GUID (`6b38c9aa-bbee-45e9-81e9-cf25707910e7`), namespace/assembly (`MarketExtension`), package identity (`CostaFotiadis.MarketExtension`), version (`0.0.1.0`). Still TODO before shipping:

- **Partner Center**: reserve the app name; confirm `Identity Name` + `Publisher` in `Package.appxmanifest`/csproj match what it assigns.
- **GitHub secrets**: add `SIGNING_CERT_PFX` + `SIGNING_CERT_PASSWORD` (see `MarketExtension/create-signing-cert.ps1`). Template clones do not carry secrets.
- **Assets**: replace the art in `MarketExtension/Assets/` (still the AdbExtension tiles/logos).
- **Sentry**: set your own DSN in `Program.cs` or leave it empty to disable.
- **API key**: create `MarketExtension/secrets.props` from `secrets.props.template` and paste your Finnhub key (gitignored; see the "API Key" section above).
- **Description**: update the placeholder description in `Package.appxmanifest`.

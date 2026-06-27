# CommandPalette Toolkit — reference

## Navigation

`Page` extends `Command` (implements `ICommand`). Pass a page anywhere a command is accepted — the palette
navigates into it automatically. No string-based registration.

```csharp
new CommandItem(new MyPage()) { Title = "My Command" }   // top-level entry point
new ListItem(new MySubPage(arg)) { Title = "Go deeper" } // navigate into a sub-page
new ListItem(new MyInvokableCommand()) { Title = "Do thing" } // run a command
```

## Base classes

| Class | Use when |
|---|---|
| `InvokableCommand` | Action with no UI — runs and returns a `CommandResult` |
| `ListPage` | Static list of items |
| `DynamicListPage` | List that reacts to search input — override `UpdateSearchText()` |
| `CommandProvider` | Extension entry point — returns top-level `ICommandItem[]` |

## Page activation hook (on-load refresh) — RECURRING NEED

The framework calls `FetchItems` → `GetItems()` **before** subscribing to `ItemsChanged`. So any
`RaiseItemsChanged` fired from the constructor is lost and the page shows empty on first open.

The fix: re-implement `INotifyItemsChanged` on the page and intercept the `add` accessor (the framework
subscribes via `INotifyItemsChanged.ItemsChanged +=`, so our accessor fires right after subscription). The
`add`/`remove` accessors are CmdPal's de-facto Loaded/Unloaded hooks — subscribe to your flows in `add`,
dispose in `remove`.

**Critical:** use `INotifyItemsChanged.ItemsChanged`, not `IListPage.ItemsChanged`, and re-list the
interface to override base-class dispatch.

```csharp
// Async-loading page (see Pages/PricedListPage.cs, Pages/MarketsPage.cs):
internal sealed partial class MyPage : DynamicListPage, INotifyItemsChanged
{
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshData(); }   // called every time the user navigates here
        remove => _itemsChanged -= value;
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));
}

// Synchronous GetItems() (see reference/pages/PackageActionsPage.cs):
//   add { _itemsChanged += value; _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(-1)); }  // fire immediately
```

Do **not** call `IsLoading = true` + `Task.Run(Load)` from the constructor — the event fires before the
framework subscribes and the signal is lost.

## ⚠️ ContentPage single-`FormContent` focus gotcha

If `GetContent()` returns a **single** `FormContent`, the host marks it `OnlyControlOnPage = true` and
**auto-focuses the card's first focusable element** (e.g. the first `Action.Submit`) on load — so Enter
activates that card button instead of the page's primary (Enter) command, and with focus trapped in the card
Ctrl+Enter can't reach the secondary command either.

Content count is the **only** documented lever (no host API suppresses the auto-focus). In theory: (a)
return **≥2 content items** so `OnlyControlOnPage` is false; or (b) order the card so the action you WANT
Enter to fire is the first focusable element. ⚠️ The symbol-detail chart tried **both and neither shipped** —
(a) did NOT keep focus in the search box in practice (Enter still hit a tab, Ctrl+Enter broke), (b) was too
hacky. It ships with the bug **unfixed**. Treat (a) as suspect until someone reproduces it working.

(`SetQuantityPage` is the benign case: a single `FormContent` whose first field is the quantity input, so
the auto-focus drops the cursor exactly where you want it.)

## CommandResult options

```csharp
CommandResult.ShowToast("message")              // toast, dismiss palette
CommandResult.ShowToast(new ToastArgs { Message = "message", Result = CommandResult.KeepOpen() }) // toast, keep open
CommandResult.KeepOpen()                         // stay on current page
CommandResult.Dismiss()  /  .GoBack()  /  .GoHome()
CommandResult.Confirm(new ConfirmationArgs())    // confirmation dialog
```

## ListItem / Icon

```csharp
new ListItem(command) {
    Title = "Required", Subtitle = "Optional", Section = "Section header",
    Icon = new IconInfo("https://..."),         // URL icon (host fetches it)
}
new IconInfo("")                           // Segoe MDL2 glyph (C# escape — see CLAUDE.md gotcha)
new IconInfo(lightIconData, darkIconData)        // separate light/dark
IconHelpers.FromRelativePath("Assets\\foo.png")  // bundled asset
```

## ProcessHelper

```csharp
// Run any external process. Reads BOTH stdout and stderr before WaitForExit (prevents pipe deadlocks).
ProcessHelper.Run(string fileName, string arguments, out string stdout, out string stderr);

// Launch a URL / file / protocol with the OS default handler (UseShellExecute=true). Fire-and-forget.
ProcessHelper.OpenUrl(string url);
```

For richer process handling (output parsing into records), see `reference/helpers/AdbHelper.cs`.

## InvokableCommand pattern (with error-toast convention)

```csharp
internal sealed partial class MyCommand : InvokableCommand
{
    public MyCommand() { Name = "Do Thing"; Icon = new IconInfo("https://..."); }

    public override ICommandResult Invoke()
    {
        try { /* work */ return CommandResult.ShowToast("Done"); }
        catch (Exception ex) when (ex is Win32Exception w && w.NativeErrorCode == 2)
            { return ErrorToast("Required tool not found. Make sure it is on your PATH."); }
        catch (Exception ex) { return ErrorToast($"Unexpected error: {ex.Message}"); }
    }

    private static ICommandResult ErrorToast(string message) =>
        CommandResult.ShowToast(new ToastArgs { Message = message, Result = CommandResult.KeepOpen() });
}
```

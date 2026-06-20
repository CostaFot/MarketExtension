using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;

namespace MarketExtension;

// Sample DynamicListPage demonstrating the on-load refresh hook (INotifyItemsChanged).
// This is the #1 gotcha: the framework calls GetItems() BEFORE subscribing to
// ItemsChanged, so a RaiseItemsChanged fired from the constructor is lost and the page
// shows empty on first open. Intercepting the `add` accessor fires a refresh right after
// the framework subscribes. Full writeup is in CLAUDE.md; a real-world version (loading
// installed packages over adb) is in reference/pages/AdbExtensionPage.cs.
internal sealed partial class SampleListPage : DynamicListPage, INotifyItemsChanged
{
    private string[]? _data;
    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add { _itemsChanged += value; RefreshData(); } // fires every time the user navigates here
        remove => _itemsChanged -= value;
    }

    protected new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public SampleListPage()
    {
        Icon = new IconInfo("https://github.com/favicon.ico");
        Title = "Sample List";
        Name = "Open";
        PlaceholderText = "Search...";
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
        => RaiseItemsChanged(0);

    public override IListItem[] GetItems()
    {
        if (_data is null)
            return [];

        var source = string.IsNullOrEmpty(SearchText)
            ? _data
            : _data.Where(x => x.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (source.Length == 0)
            return [new ListItem(new NoOpCommand()) { Title = "No results" }];

        return source
            .Select(x => (IListItem)new ListItem(new SampleCommand()) { Title = x })
            .ToArray();
    }

    internal void RefreshData()
    {
        _data = null;
        IsLoading = true;
        RaiseItemsChanged(0);
        Task.Run(LoadData);
    }

    private void LoadData()
    {
        // Replace with a real (possibly slow) data source.
        _data = ["Alpha", "Bravo", "Charlie"];
        IsLoading = false;
        RaiseItemsChanged(0);
    }
}

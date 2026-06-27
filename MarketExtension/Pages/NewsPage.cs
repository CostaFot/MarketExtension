using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Top-level "Markets - News" command: a list of market-news headlines for the selected view, each row opening
// the original article in the browser (OpenUrlCommand). A dropdown (the toolkit Filters) switches between
// "All" (the merged feed across every category, the default) and the individual feeds General / Forex /
// Crypto / Mergers; typing in the search box filters the current feed's headlines client-side (no network).
//
// A PURE OBSERVER of the shared news cache, like the dock bands are of the quote cache: while visible it
// subscribes to MarketRepository.ObserveNews(selected-category flow) and renders whatever it emits. It does
// NOT fetch or poll itself — the repository owns that (one news poll loop on the separate news cadence, plus
// demo-flip refills) — so this page and a future news dock band reading the same category can never drift
// apart. Switching the category filter pushes the new category into _category, and the ObserveNews StateFlow
// overload's Switch re-projects to that category's cache stream.
//
// Threading: ObserveNews delivers via ObserveOn (see MarketRepository), so OnNewsChanged — and therefore
// RaiseItemsChanged — runs on a pool thread with NO Rx gate lock held. That is the deadlock-safe pattern the
// dock bands use; do not wrap the subscribe in Task.Run or add ObserveOn here.
internal sealed partial class NewsPage : DynamicListPage, INotifyItemsChanged
{
    // Segoe MDL2 ReportDocument — the row icon when an article carries no thumbnail image. A C# \u escape
    // (not a literal glyph char) so the Write tool can't drop it.
    private const string ArticleGlyph = "\uE9F9";

    private readonly MarketRepository _repository;
    private readonly NewsCategoryFilters _filters = new();

    // The selected view, driven by the Filters dropdown: null = "All" (the merged feed across every category,
    // the default), else a single category. ObserveNews(StateFlow) re-projects (Switch) when this changes, so
    // flipping the filter swaps the observed feed without re-subscribing the page.
    private readonly MutableStateFlow<NewsCategory?> _category = new(null);

    private UiNews[]? _news; // latest cache emission, projected for rendering; null before the first emission

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    // Held in a list (not a single field) so a double-`add` without an intervening `remove` can't orphan a
    // subscription — which would pin the category to the repository's news poll loop forever. Matches the docks.
    private readonly List<IDisposable> _subscriptions = [];

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // Observe the selected category's cached feed while the page is visible. The StateFlow overload
            // replays the current category's cache on subscribe (fetching it if missing/stale), re-projects on
            // a category change, and re-emits when the repository's poll/demo refresh updates that feed.
            // Delivery is off-thread (ObserveOn in the repo), so the first emission lands after this accessor
            // returns. Disposed in `remove` so a hidden page does no work and unregisters its category.
            _subscriptions.Add(_repository.ObserveNews(_category).Subscribe(OnNewsChanged));
            // Re-render when throttling starts/stops (the rate-limit banner) and when a key is added/cleared
            // (the no-key status row), so each appears/disappears at once. replay:false — the observe
            // subscription above already drives the initial paint. Mirrors PricedListPage.
            _subscriptions.Add(RateLimitSignal.Instance.IsRateLimited
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            _subscriptions.Add(MarketSettingsManager.Instance.HasAnyApiKey
                .Subscribe(_ => RaiseItemsChanged(0), replayOnSubscribe: false));
            RecomputeLoading();
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public NewsPage(MarketRepository repository)
    {
        _repository = repository;
        Id = "com.costafotiadis.market.news";
        Title = Resources.Command_MarketsNews;
        Name = Resources.Action_Open;
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
        PlaceholderText = Resources.News_Placeholder;
        ShowDetails = true; // show the side preview pane (summary + thumbnail)
        IsLoading = true;   // spinner until the first feed lands

        _filters.PropChanged += OnFilterChanged;
        Filters = _filters;
    }

    // Typing filters the current category's headlines client-side; it never hits the network. The category
    // dropdown, not typing, switches feeds.
    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged(0);

    // True when news can't be served right now: demo mode is off AND no Finnhub key is set (Finnhub is the
    // only live news provider; an unset key makes /news return nothing forever). In that state we show the
    // no-key status row instead of an endless spinner — gated on HasFinnhubApiKey, NOT the broader
    // HasAnyApiKey, since a Twelve-Data-only key can't serve news.
    private static bool NewsUnavailable =>
        !MarketSettingsManager.Instance.DemoMode && !MarketSettingsManager.Instance.HasFinnhubApiKey;

    // The single source of truth for the spinner. Loading only when news IS serviceable AND we're genuinely
    // still waiting: no emission yet, or an empty feed whose fetch for the current selection hasn't completed.
    // No-key (covered by the status row) and loaded-but-empty (fetch done, nothing came back) are NOT loading.
    private void RecomputeLoading() =>
        IsLoading = !NewsUnavailable
                    && (_news is null
                        || (_news.Length == 0 && !_repository.HasAttemptedNewsFetch(_category.Value)));

    public override IListItem[] GetItems()
    {
        var items = new List<IListItem>();

        // Rate-limited banner pinned at the TOP so it's seen without scrolling — the "All" view fans out four
        // /news calls per tick, so the feed can get throttled. Parallels the priced pages / Search. #4.
        if (RateLimitHint.Row() is { } banner)
            items.Add(banner);

        // No news provider available (demo off + no Finnhub key): explain it instead of spinning forever. This
        // is the page's whole body in that state, so return right after it. #2.
        if (NewsUnavailable)
        {
            if (ApiKeyHint.NewsStatusRow() is { } noKey)
                items.Add(noKey);
            return [.. items];
        }

        var news = _news;
        if (news is null)
            return [.. items]; // before the first emission — the IsLoading spinner covers it

        var query = SearchText?.Trim() ?? string.Empty;
        var rows = query.Length == 0 ? news : [.. news.Where(n => n.Matches(query))];

        if (news.Length == 0)
        {
            // Empty feed: "still loading" vs "loaded-but-empty" (the cache emits empty for both). Only show the
            // empty-state row once a fetch for this selection has actually completed; else leave the spinner.
            if (_repository.HasAttemptedNewsFetch(_category.Value))
                items.Add(EmptyStateRow());
        }
        else if (rows.Length == 0) // a typed query matched nothing
        {
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = Strings.Format(Resources.Search_NoMatches, query),
            });
        }
        else
        {
            items.AddRange(rows.Select(BuildRow));
        }

        // Demo "showing sample data" row at the bottom (informational), consistent with the other screens —
        // NewsStatusRow returns the demo row when demo mode is on, null when a live Finnhub key is set.
        if (ApiKeyHint.NewsStatusRow() is { } status)
            items.Add(status);

        return [.. items];
    }

    // Shown when a fetch completed but returned no headlines (rare for market-wide news, but it keeps the page
    // honest rather than spinning forever).
    private static ListItem EmptyStateRow() =>
        new(new NoOpCommand())
        {
            Title = Resources.News_Empty_Title,
            Subtitle = Resources.News_Empty_Subtitle,
        };

    // One headline row: Enter opens the article; the thumbnail (or a document glyph) is the icon; the summary
    // and a larger thumbnail fill the Details preview pane.
    private static ListItem BuildRow(UiNews n)
    {
        var image = string.IsNullOrWhiteSpace(n.ImageUrl) ? null : new IconInfo(n.ImageUrl);
        var details = new Details
        {
            Title = n.Headline,
            Body = n.Summary,
        };
        if (image is not null)
            details.HeroImage = image; // only set when present — IconInfo has no parameterless ctor to default to

        return new ListItem(new OpenUrlCommand(n.ArticleUrl, Resources.Action_ReadArticle))
        {
            Title = n.Headline,
            Subtitle = n.FormatSubtitle(),
            Icon = image ?? new IconInfo(ArticleGlyph),
            Details = details,
        };
    }

    // A new cache emission for the selected category: project to UiNews and repaint. Runs on a pool thread
    // (ObserveOn) — no Rx gate lock is held, so RaiseItemsChanged's host call is safe.
    private void OnNewsChanged(IReadOnlyList<DomainNews> news)
    {
        _news = [.. news.Select(UiNews.From)];
        RecomputeLoading(); // empty == loading only until the fetch completes; no-key never loads (see RecomputeLoading)
        RaiseItemsChanged(0);
    }

    // The category dropdown changed: push the new selection into the flow (ObserveNews re-projects via Switch)
    // and show the spinner immediately while the new feed loads.
    private void OnFilterChanged(object sender, IPropChangedEventArgs args)
    {
        _news = null;
        _category.Update(NewsCategoryFilters.ToSelection(_filters.CurrentFilterId));
        RecomputeLoading(); // reads the just-updated _category; spinner unless news is unavailable
        RaiseItemsChanged(0);
    }

    // The category dropdown shown above the list: an "All" entry (the merged view — default-selected) followed
    // by one entry per NewsCategory. The selected id is "All" or the NewsCategory name (round-tripped via
    // ToSelection); the visible name is NewsCategory.Label() (or the localized "All").
    private sealed partial class NewsCategoryFilters : Filters
    {
        // The "All" entry's id — a sentinel distinct from any NewsCategory name. Selected by default.
        private const string AllId = "All";

        public NewsCategoryFilters() => CurrentFilterId = AllId;

        public override IFilterItem[] GetFilters() =>
        [
            new Filter { Id = AllId, Name = Resources.News_Category_All },
            .. NewsCategoryExtensions.All.Select(c => (IFilterItem)new Filter
            {
                Id = c.ToString(),
                Name = c.Label(),
            }),
        ];

        // The selected view: null for "All" (the AllId sentinel, or any unrecognized id), else the category.
        public static NewsCategory? ToSelection(string? id) =>
            Enum.TryParse<NewsCategory>(id, out var category) ? category : null;
    }
}

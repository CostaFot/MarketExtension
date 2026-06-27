using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation;
using MarketExtension.Properties;

namespace MarketExtension;

// Backs a third Command Palette Dock band (next to favorites + portfolio): a market-news ticker. It renders a
// ROW of headline buttons — each one a real headline that opens its own article — and cycles the row through
// the feed one headline at a time on a slow timer, like a TV news crawl. Returned from
// MarketExtensionCommandsProvider.GetDockBands() wrapped in a CommandItem.
//
// Why a row of buttons (not a single scrolling Title): the host caps each dock button's Title at MaxWidth=100
// DIPs (Segoe UI 12 → ~15 chars, CharacterEllipsis) — see PowerToys DockItemControl.xaml. A single button can
// therefore only ever show ~15 chars, and a Title is left-aligned static text the host does NOT pixel-scroll,
// so a smooth one-button crawl is impossible (advancing by a character just repaints a different ~15-char
// snapshot — it pops, it doesn't slide). A dock-band ListPage instead renders EACH GetItems() item as its own
// ≤100px button (that's how the favorites band shows several tickers), so a row of N buttons is a wider,
// readable, jitter-free strip. We "go through the headlines" by advancing which N of the feed are shown on a
// configurable cycle (NewsTickerCycleInterval, default 60s; offset += 1), so the row scrolls headline-by-headline.
//
// A PURE OBSERVER of the shared news cache, like NewsPage and the priced dock bands: while visible it
// subscribes to MarketRepository.ObserveNews(null selection = the merged "All" feed) and renders what it
// emits. It does NOT fetch or poll itself — the repository owns the single news poll loop (gentle ~30-min
// news cadence) and the demo-flip refill — so the band and the News screen can never drift apart. Subscribing
// registers the band as an observer so the repo keeps the feed fresh; disposing on hide unregisters. "All"
// fans out (ObserveAllNewsCore CombineLatest's all four categories), so a pinned band keeps all four news
// categories warm — expected, identical to NewsPage with "All" selected, and cheap at the news cadence.
//
// Threading: ObserveNews delivers via ObserveOn(TaskPoolScheduler) (see MarketRepository), and the cycle
// Observable.Interval also fires on TaskPoolScheduler — so BOTH OnNewsChanged and OnCycle (and therefore
// RaiseItemsChanged's blocking COM call into the host) run on a pool thread with NO Rx gate lock held. That is
// the deadlock-safe pattern the other bands use; do not add Task.Run / SubscribeOn here.
internal sealed partial class NewsDockPage : ListPage, INotifyItemsChanged
{
    private const int VisibleCount = 4;     // headline buttons shown at once (each host-capped at ~15 chars)
    // Segoe MDL2 ReportDocument (U+E9F9) - fallback icon when an article has no thumbnail. Built from its
    // code point (plain ASCII hex), so there is no literal glyph or escape for the Write tool to mangle.
    private static readonly string ArticleGlyph = ((char)0xE9F9).ToString();

    private readonly MarketRepository _repository;
    // Always the merged "All" feed: a null selection. Held as a field so it outlives the subscription; never
    // Update()d (the band has no category dropdown — it shows everything, like a real news crawl).
    private readonly MutableStateFlow<NewsCategory?> _category = new(null);
    // One cached NewsPage instance, reused as the placeholder button's command (loading / no-key / empty), so a
    // repaint doesn't churn a new page object.
    private readonly NewsPage _newsPage;

    // Latest feed, projected for rendering. Written by the news pool thread (OnNewsChanged), read by the cycle
    // thread (OnCycle) and the host thread (GetItems). null = loading; empty = loaded-but-empty / no-key.
    // A whole-reference assignment, so cross-thread reads are atomic (no lock) — same as the other dock pages.
    private volatile UiNews[]? _news;
    // The index of the first visible headline; advanced by OnCycle (the sole writer). volatile for visibility
    // to the host thread's GetItems read. Always taken mod the feed length, so it can't index out of range.
    private volatile int _offset;

    private event TypedEventHandler<object, IItemsChangedEventArgs>? _itemsChanged;
    // Subscriptions in a list (not a single field) so a double-`add` without an intervening `remove` can't
    // orphan one — which here would leave the cycle Interval repainting a hidden band forever AND pin the news
    // categories to the repo's poll loop. Dispose-all-and-clear in `remove` keeps every subscribe balanced.
    private readonly List<IDisposable> _subscriptions = [];

    event TypedEventHandler<object, IItemsChangedEventArgs> INotifyItemsChanged.ItemsChanged
    {
        add
        {
            _itemsChanged += value;
            // (1) The data feed: observe the merged "All" news feed while the band is visible. Replays the
            //     current cache on subscribe (fetching if missing/stale), re-emits on the repo's poll/demo
            //     refresh. Delivery is off-thread (ObserveOn in the repo).
            _subscriptions.Add(_repository.ObserveNews(_category).Subscribe(OnNewsChanged));
            // (2) The cycle clock: a self-rescheduling timer that advances which headlines are shown. The delay
            //     is re-read from settings each tick (NewsTickerCycleInterval, default 60s), so a speed change
            //     applies on the next advance with no reload — the live-settings trick PollTicker uses. Fires on
            //     TaskPoolScheduler (same off-thread rule as ObserveNews → RaiseItemsChanged is deadlock-safe).
            //     MUST be disposed in `remove` (it's in _subscriptions) or it keeps repainting a hidden band.
            _subscriptions.Add(Observable
                .Generate(
                    initialState: 0L,
                    condition: _ => true,
                    iterate: tick => tick + 1,
                    resultSelector: tick => tick,
                    timeSelector: _ => MarketSettingsManager.Instance.NewsTickerCycleInterval,
                    scheduler: TaskPoolScheduler.Default)
                .Subscribe(OnCycle));
            Log.Info("Dock", "observing news ticker");
        }
        remove
        {
            _itemsChanged -= value;
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
            _subscriptions.Clear();
            Log.Info("Dock", "stopped observing news ticker");
        }
    }

    private new void RaiseItemsChanged(int totalItems = -1)
        => _itemsChanged?.Invoke(this, new ItemsChangedEventArgs(totalItems));

    public NewsDockPage(MarketRepository repository)
    {
        _repository = repository;
        _newsPage = new NewsPage(_repository);
        Id = "com.costafotiadis.market.dock.news"; // dock bands require a non-empty command Id
        Title = Resources.Command_MarketsNews;
        Icon = IconHelpers.FromRelativePath("Assets\\markets_logo_base_square.png");
    }

    public override IListItem[] GetItems()
    {
        var news = _news;
        if (news is null)
            return []; // before the first emission — loading; nothing to show yet

        if (news.Length == 0)
            // Loaded-but-empty / no-key: keep the band present with one placeholder button that opens the full
            // News screen (which surfaces the no-key / empty state properly).
            return [new ListItem(_newsPage) { Title = Resources.Command_MarketsNews }];

        // Show VisibleCount consecutive headlines starting at _offset, wrapping around the feed. Snapshot
        // _offset once so a concurrent OnCycle advance can't shift the window mid-build.
        var count = news.Length;
        var take = Math.Min(VisibleCount, count);
        var offset = _offset;
        var items = new IListItem[take];
        for (var i = 0; i < take; i++)
            items[i] = BuildButton(news[(offset + i) % count]);
        return items;
    }

    // One headline button: Enter opens THAT article in the browser; the thumbnail (or a document glyph) is the
    // icon; the source · age is the subtitle. A stable per-article Id (like the favorites band sets on its dock
    // items) keeps item identity steady across repaints.
    private static ListItem BuildButton(UiNews n) =>
        new(new OpenUrlCommand(n.ArticleUrl, Resources.Action_ReadArticle)
        {
            Id = $"com.costafotiadis.market.dock.news.{n.Source.Id}",
        })
        {
            Title = n.Headline,
            Subtitle = n.FormatSubtitle(),
            Icon = string.IsNullOrWhiteSpace(n.ImageUrl) ? new IconInfo(ArticleGlyph) : new IconInfo(n.ImageUrl),
        };

    // A new feed emission: project to UiNews and reset the window to the freshest headlines. Runs on a pool
    // thread (ObserveOn) with no Rx gate lock held, so RaiseItemsChanged's host call is safe.
    private void OnNewsChanged(IReadOnlyList<DomainNews>? news)
    {
        _news = news is null ? null : [.. news.Select(UiNews.From)];
        _offset = 0; // lead with the newest headlines again after each ~30-min refresh
        Log.Info("Dock", news is null ? "news ticker: loading" : $"news ticker: {news.Count} headline(s)");
        RaiseItemsChanged(0);
    }

    // The cycle step: advance the visible window by one headline so the row scrolls headline-by-headline. Skips
    // when there's nothing to cycle (loading, empty, or the whole feed already fits) — no needless repaint.
    // Guarded so a transient host hiccup can't tear down the Interval and freeze the cycle (PollTicker's guard
    // precedent).
    private void OnCycle(long _)
    {
        try
        {
            var news = _news;
            if (news is null || news.Length <= VisibleCount)
                return;
            _offset = (_offset + 1) % news.Length;
            RaiseItemsChanged(0);
        }
        catch (Exception ex)
        {
            Log.Error("Dock", "news ticker cycle failed — continuing", ex);
        }
    }
}

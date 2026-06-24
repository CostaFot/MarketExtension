using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;

namespace MarketExtension;

// A minimal StateFlow analog (Kotlin's StateFlow): holds a current value, REPLAYS it to every new
// subscriber the moment they subscribe, then pushes the new value on each change. Deduplicates with an
// equality comparer (distinct-until-changed).
//
// Implemented as a thin wrapper over System.Reactive's BehaviorSubject<T> (which already gives us the
// current value, replay-on-subscribe, and thread-safe observer fan-out). The hand-rolled version this
// replaced avoided System.Reactive only because the build was once AOT/trim-clean; that constraint is
// gone (AOT/trim is a deliberate non-goal now — see CLAUDE.md), so we take the dependency to unlock Rx's
// operator library for the deferred work (429 Retry/RetryWhen back-off, Throttle'd search, Merge'd
// providers, Observable.Timer polling). The PUBLIC API is unchanged, so pages, the dock, and PollTicker
// don't change.
//
// This is the read-only face consumers see (read Value / Subscribe). Only the writable MutableStateFlow
// subclass can change the value — mirroring Kotlin's MutableStateFlow/StateFlow split, so the store
// mutates while pages and the dock merely observe.
//
// Subscriber-count awareness: OnActive() fires when the subscriber count goes 0 -> 1 and OnInactive()
// when it returns 1 -> 0. That is the WhileSubscribed / Rx RefCount() seam — PollTicker starts its poll
// loop on OnActive and stops it on OnInactive, so it only does work while something is actually watching.
// (BehaviorSubject has no refcount hook, so we keep counting subscribers by hand here — this IS the
// Publish().RefCount() seam, kept explicit so the hooks stay a clean override point.) Plain flows leave
// both hooks no-op.
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The BehaviorSubject is owned by process-lifetime singletons (WatchlistStore, " +
                    "MarketSettingsManager, PollTicker) whose flows are observed for the life of the " +
                    "process; it is intentionally never completed or disposed.")]
internal partial class StateFlow<T>
{
    private readonly BehaviorSubject<T> _subject;
    private readonly IEqualityComparer<T> _comparer;
    private readonly Lock _countGate = new();
    private int _count;

    protected StateFlow(T initial, IEqualityComparer<T>? comparer = null)
    {
        _subject = new BehaviorSubject<T>(initial);
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public T Value => _subject.Value;

    // Subscribe and immediately receive the current value (StateFlow replay — BehaviorSubject does this).
    // Dispose to unsubscribe; disposal is idempotent. Handlers run on whatever thread mutates the value
    // (BehaviorSubject fans out OUTSIDE its internal lock) — the toolkit marshals the RaiseItemsChanged
    // that handlers ultimately call.
    public IDisposable Subscribe(Action<T> onNext) => Subscribe(onNext, replayOnSubscribe: true);

    // As above, but pass replayOnSubscribe:false to suppress the initial replay and only receive
    // *future* values. The poll ticker uses this: a page's first price load is already driven by its
    // membership flow's replay, so a replayed tick would just double-fetch on every open. OnActive still
    // fires on the 0 -> 1 transition regardless (so the producer starts), only the replay is skipped —
    // Skip(1) drops the value BehaviorSubject replays synchronously on subscribe.
    public IDisposable Subscribe(Action<T> onNext, bool replayOnSubscribe)
    {
        ArgumentNullException.ThrowIfNull(onNext);

        bool becameActive;
        lock (_countGate) becameActive = ++_count == 1;
        if (becameActive) OnActive(); // wake any producer before the replay-on-subscribe below

        IObservable<T> source = replayOnSubscribe ? _subject : _subject.Skip(1);
        var inner = source.Subscribe(onNext); // BehaviorSubject replays the current value here (unless skipped)
        return new Subscription(this, inner);
    }

    // Writable entry point for subclasses. Returns true if the value actually changed (i.e. listeners
    // were notified). Distinct-until-changed at the source: an equal value is not pushed, so re-publishing
    // an unchanged subset doesn't wake its subscribers. OnNext invokes handlers OUTSIDE the subject's
    // lock — handlers re-read the store, which re-takes the store lock, with no lock held here.
    protected bool SetValue(T value)
    {
        if (_comparer.Equals(_subject.Value, value)) return false; // distinct-until-changed
        _subject.OnNext(value);
        return true;
    }

    // Called on the 0 -> 1 subscriber transition (the flow "becomes active"). No-op by default.
    protected virtual void OnActive() { }

    // Called on the 1 -> 0 subscriber transition (the flow "goes idle"). No-op by default.
    protected virtual void OnInactive() { }

    private void Unsubscribe()
    {
        bool becameIdle;
        lock (_countGate) becameIdle = --_count == 0;
        if (becameIdle) OnInactive();
    }

    private sealed partial class Subscription(StateFlow<T> flow, IDisposable inner) : IDisposable
    {
        private StateFlow<T>? _flow = flow;

        public void Dispose()
        {
            // Null out atomically so a double-dispose can't decrement the count twice.
            var owner = Interlocked.Exchange(ref _flow, null);
            if (owner is null) return;
            inner.Dispose();   // detach from the BehaviorSubject
            owner.Unsubscribe();
        }
    }
}

// The writable side of a StateFlow — only the data owner (e.g. WatchlistStore) holds one of these and
// exposes it typed as the read-only StateFlow<T>.
internal sealed class MutableStateFlow<T>(T initial, IEqualityComparer<T>? comparer = null)
    : StateFlow<T>(initial, comparer)
{
    public bool Update(T value) => SetValue(value);
}

// Compares two instrument lists by their symbol sequence (order-sensitive, case-insensitive). Used as
// the dedup comparer for the watchlist/favorites flows so re-publishing both subsets after a mutation
// only notifies the one whose membership actually changed.
internal sealed class InstrumentListComparer : IEqualityComparer<IReadOnlyList<DomainInstrument>>
{
    public static readonly InstrumentListComparer Instance = new();

    private InstrumentListComparer() { }

    public bool Equals(IReadOnlyList<DomainInstrument>? x, IReadOnlyList<DomainInstrument>? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Count != y.Count) return false;

        for (var i = 0; i < x.Count; i++)
            if (!string.Equals(x[i].Symbol, y[i].Symbol, StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    public int GetHashCode(IReadOnlyList<DomainInstrument> obj)
    {
        var hash = new HashCode();
        foreach (var instrument in obj)
            hash.Add(instrument.Symbol, StringComparer.OrdinalIgnoreCase);
        return hash.ToHashCode();
    }
}

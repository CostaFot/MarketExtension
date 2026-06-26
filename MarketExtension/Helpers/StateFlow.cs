using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MarketExtension;

// A minimal StateFlow analog (Kotlin's StateFlow): holds a current value, REPLAYS it to every new
// subscriber the moment they subscribe, then pushes the new value on each change. Deduplicates with an
// equality comparer (distinct-until-changed).
//
// A thin wrapper over System.Reactive's BehaviorSubject<T>, which already provides the current value,
// replay-on-subscribe, fan-out, and thread-safe, idempotent subscriptions. The hand-rolled version this
// replaced avoided System.Reactive only because the build was once AOT/trim-clean; that constraint is gone
// (AOT/trim is a deliberate non-goal now — see CLAUDE.md), so we take the dependency to unlock Rx's
// operator library for the deferred work (429 Retry/RetryWhen back-off, Throttle'd search, Merge'd
// providers). The PUBLIC API is unchanged, so the state holders (WatchlistStore, MarketSettingsManager)
// and their page/dock subscribers don't change.
//
// This is the read-only face consumers see (read Value / Subscribe). Only the writable MutableStateFlow
// subclass can change the value — mirroring Kotlin's MutableStateFlow/StateFlow split, so the store
// mutates while pages and the dock merely observe. (An earlier OnActive/OnInactive subscriber-count seam
// lived here too; it was removed once its only user, PollTicker, moved to pure Rx Publish().RefCount() —
// see PollTicker.cs.)
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The BehaviorSubject is owned by process-lifetime singletons (WatchlistStore, " +
                    "MarketSettingsManager) whose flows are observed for the life of the process; it is " +
                    "intentionally never completed or disposed.")]
internal class StateFlow<T>
{
    private readonly BehaviorSubject<T> _subject;
    private readonly IEqualityComparer<T> _comparer;

    protected StateFlow(T initial, IEqualityComparer<T>? comparer = null)
    {
        _subject = new BehaviorSubject<T>(initial);
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public T Value => _subject.Value;

    // Subscribe and immediately receive the current value (StateFlow replay — BehaviorSubject does this).
    // Dispose to unsubscribe; Rx subscriptions are idempotent on dispose. Handlers run on whatever thread
    // mutates the value (BehaviorSubject fans out OUTSIDE its internal lock) — the toolkit marshals the
    // RaiseItemsChanged that handlers ultimately call.
    public IDisposable Subscribe(Action<T> onNext) => Subscribe(onNext, replayOnSubscribe: true);

    // As above, but pass replayOnSubscribe:false to suppress the initial replay and only receive *future*
    // values (Skip(1) drops the value BehaviorSubject replays synchronously on subscribe). Used where a
    // surface's initial paint is already driven by another flow's replay, so replaying here would just
    // duplicate work — e.g. the priced pages re-rendering on a HasAnyApiKey change.
    public IDisposable Subscribe(Action<T> onNext, bool replayOnSubscribe)
    {
        ArgumentNullException.ThrowIfNull(onNext);

        // Guard the handler so a throw can never escape into Rx. Two reasons:
        //  1. Symmetry: the replayOnSubscribe:false path runs through Skip(1) — an Rx operator whose
        //     SafeObserver DISPOSES the subscription if OnNext throws (silently unsubscribing the surface),
        //     while the raw-BehaviorSubject replay path does not. Guarding makes both paths behave the same
        //     (throw is non-fatal to the subscription), matching the old hand-rolled flow.
        //  2. Isolation: BehaviorSubject fans out with a foreach, so one subscriber throwing would skip the
        //     rest for that emission. Swallowing here keeps subscribers independent.
        // Errors are logged via Log.Error (Debug builds only), tagged StateFlow.
        void Guarded(T value)
        {
            try { onNext(value); }
            catch (Exception ex) { Log.Error("StateFlow", "subscriber handler threw", ex); }
        }

        IObservable<T> source = replayOnSubscribe ? _subject : _subject.Skip(1);
        return source.Subscribe(Guarded);
    }

    // The underlying stream, for Rx composition (CombineLatest/Switch in MarketRepository.ObserveQuotes).
    // Replays the current value on subscribe, like Subscribe(). Bypasses the Guarded wrapper deliberately —
    // composition operators manage their own errors, and these flows never OnError.
    public IObservable<T> AsObservable() => _subject.AsObservable();

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

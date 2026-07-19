using System.ComponentModel;
using System.Threading;
using System.Windows.Threading;
using LocalScribe.Core.Live;

namespace LocalScribe.App.Services;

/// <summary>
/// Stale-toast guard (M3, branch-4 whole-branch review) extracted to a WPF-free, unit-testable
/// class. A stop-confirm toast MUST never stop a LATER session than the one it was raised for:
/// recording A ends, the user starts B, clicks the old toast -&gt; without this it would stop B.
/// <para>
/// CORRECTED INVARIANT (the fix): a stop-confirm toast raised while a recording epoch exists
/// (State != Idle) must auto-close the instant the app returns to <see cref="SessionState.Idle"/>
/// - the single point BEFORE any new recording can start - so it can never span into a later
/// session. The earlier inline guard only armed for Recording/Paused, so a toast raised during
/// the multi-second <see cref="SessionState.Finalizing"/> drain (State != Idle, the advisor still
/// armed) installed NO guard and lingered past Idle into session B. Finalizing is a live epoch;
/// Idle is the boundary. This guard watches for that Idle boundary regardless of the entry state.
/// </para>
/// <para>
/// Testability: it observes an <see cref="INotifyPropertyChanged"/> state source and reads the
/// current state through an injected <see cref="Func{SessionState}"/>, so a unit test can drive
/// Recording -&gt; Finalizing -&gt; Idle -&gt; Recording deterministically with a tiny fake source and
/// no async controller. Production passes the SessionViewModel and <c>() =&gt; session.State</c>;
/// the injected <c>onEpochEnded</c> is <c>toast.Close()</c>. It fires <c>onEpochEnded</c> AT MOST
/// ONCE, at the first transition to Idle after construction, then unsubscribes. Single-threaded by
/// contract: constructed and notified only on the WPF dispatcher thread (SessionViewModel marshals
/// its StateChanged through the UI dispatch), so no locking is needed.
/// </para>
/// <para>
/// COMPLETE INVARIANT (M3 re-review hardening): a millisecond-wide race remained even after the
/// fix above - a call-end tick can observe Finalizing and queue this class's construction via
/// Dispatcher.BeginInvoke, but if the StopAsync continuation reaches Idle first, the guard gets
/// constructed AFTER the epoch already ended. The old ctor treated that as "no epoch, do
/// nothing," which left the toast open and stale (session B could then click it and get stopped).
/// The complete invariant is: onEpochEnded fires exactly once, at the first moment State is (or
/// becomes) Idle at-or-after construction - so an Idle-at-construction toast closes right away
/// instead of lingering. Ordering hazard verified against the one production call site
/// (App.xaml.cs's ShowStopConfirmToast): the guard is constructed BEFORE toast.Show() runs later
/// in that same synchronous method, so firing onEpochEnded() inline in the ctor would close the
/// AdvisoryToastWindow before it is ever shown, and the following toast.Show() would then throw
/// (WPF forbids Show() on a window that is already Closing/Closed). The WPF UI thread always runs
/// under a <see cref="DispatcherSynchronizationContext"/>, so this posts the close through it
/// instead of calling it inline - deferring it past the rest of that synchronous method (and
/// therefore past Show()). Everywhere else - including every unit test here, where
/// <see cref="SynchronizationContext.Current"/> is either null or xunit's own ambient
/// <c>AsyncTestSyncContext</c> (installed around every test, sync or async - "any non-null
/// context" is not a safe stand-in for "the real WPF UI thread") - the close runs synchronously in
/// the ctor, which is what <c>Constructed_while_Idle_closes_immediately</c> asserts.
/// </para>
/// </summary>
public sealed class StopConfirmToastGuard : IDisposable
{
    private readonly INotifyPropertyChanged _source;
    private readonly Func<SessionState> _readState;
    private readonly Action _onEpochEnded;
    private bool _done;   // set once the epoch ended (or there was none) - guarantees at-most-once

    /// <param name="source">The recording-state source to observe (production: the SessionViewModel).</param>
    /// <param name="readState">Reads the current session state (production: <c>() =&gt; session.State</c>).</param>
    /// <param name="onEpochEnded">Invoked exactly once, at the first Idle at-or-after
    /// construction (production: <c>toast.Close()</c>). If constructed while already Idle, it
    /// fires right away - synchronously off the real WPF UI thread (e.g. in tests), or deferred
    /// one tick via the ambient <see cref="DispatcherSynchronizationContext"/> on it (see the
    /// class-level "COMPLETE INVARIANT" remarks for why an inline call there is unsafe at the
    /// production call site).</param>
    public StopConfirmToastGuard(
        INotifyPropertyChanged source, Func<SessionState> readState, Action onEpochEnded)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _onEpochEnded = onEpochEnded ?? throw new ArgumentNullException(nameof(onEpochEnded));

        // Already Idle at construction: there is no live epoch to protect, and per the complete
        // invariant (M3 re-review) a toast built in this state is ALREADY stale - it must close
        // now, not linger open for a later session to click. Mark done first so Dispose (wired to
        // the toast's Closed handler) is a safe no-op no matter how the close below is scheduled.
        if (_readState() == SessionState.Idle)
        {
            _done = true;
            FireOnEpochEnded();
            return;
        }
        _source.PropertyChanged += OnSourceChanged;
    }

    /// <summary>Fires <see cref="_onEpochEnded"/> for the Idle-at-construction path. See the
    /// class-level "COMPLETE INVARIANT" remarks: calling it inline here is unsafe at the one
    /// production call site (App.xaml.cs's ShowStopConfirmToast constructs this guard BEFORE
    /// calling toast.Show() later in that same synchronous method), so specifically on the real
    /// WPF UI thread (identified by its <see cref="DispatcherSynchronizationContext"/> - the type
    /// WPF installs as <see cref="SynchronizationContext.Current"/> for every UI thread) this
    /// posts instead of calling inline, deferring past the rest of that method and therefore past
    /// Show(). A plain null/type check rather than "any non-null context" matters here: xunit
    /// installs its own ambient <c>AsyncTestSyncContext</c> around every test - including plain
    /// synchronous [Fact]s - so "non-null" alone would misfire in tests too. Anywhere else
    /// (no context, or a non-WPF one - every unit test here) it runs synchronously, matching
    /// <c>Constructed_while_Idle_closes_immediately</c>.</summary>
    private void FireOnEpochEnded()
    {
        if (SynchronizationContext.Current is DispatcherSynchronizationContext dispatcherCtx)
            dispatcherCtx.Post(_ => _onEpochEnded(), null);
        else
            _onEpochEnded();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        // React to any property notification and re-read the state; the one-shot flag makes the
        // watched-property name irrelevant (State's change also raises the derived IsIdle et al.).
        if (_done) return;
        if (_readState() != SessionState.Idle) return;   // Recording/Paused/Finalizing: still live
        _done = true;
        _source.PropertyChanged -= OnSourceChanged;       // one-shot: the epoch is over
        _onEpochEnded();
    }

    /// <summary>Unsubscribes without firing (called from the toast's Closed handler so a user click
    /// or auto-dismiss detaches the handler from the app-lifetime SessionViewModel). Idempotent and
    /// a no-op if the epoch already ended.</summary>
    public void Dispose()
    {
        if (_done) return;
        _done = true;
        _source.PropertyChanged -= OnSourceChanged;
    }
}

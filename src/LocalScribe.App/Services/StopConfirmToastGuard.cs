using System.ComponentModel;
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
/// </summary>
public sealed class StopConfirmToastGuard : IDisposable
{
    private readonly INotifyPropertyChanged _source;
    private readonly Func<SessionState> _readState;
    private readonly Action _onEpochEnded;
    private bool _done;   // set once the epoch ended (or there was none) - guarantees at-most-once

    /// <param name="source">The recording-state source to observe (production: the SessionViewModel).</param>
    /// <param name="readState">Reads the current session state (production: <c>() =&gt; session.State</c>).</param>
    /// <param name="onEpochEnded">Invoked exactly once at the first Idle after a live epoch
    /// (production: <c>toast.Close()</c>). Never invoked if constructed while already Idle.</param>
    public StopConfirmToastGuard(
        INotifyPropertyChanged source, Func<SessionState> readState, Action onEpochEnded)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _readState = readState ?? throw new ArgumentNullException(nameof(readState));
        _onEpochEnded = onEpochEnded ?? throw new ArgumentNullException(nameof(onEpochEnded));

        // No live epoch to protect: a stop-confirm toast raised while already Idle has an inert
        // Stop button (CanExecute is false when Idle) and no session identity to guard. Treat as
        // done - never subscribe, never fire, Dispose is a no-op. This path is defensive; a real
        // stop-confirm toast is only ever raised for a running/finalizing recording.
        if (_readState() == SessionState.Idle)
        {
            _done = true;
            return;
        }
        _source.PropertyChanged += OnSourceChanged;
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

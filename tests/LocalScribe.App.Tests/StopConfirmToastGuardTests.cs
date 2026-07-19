using System.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class StopConfirmToastGuardTests
{
    // M3 stale-toast guard (branch-4 whole-branch review). A stop-confirm toast must never stop a
    // LATER session than the one it was raised for. The guard closes the toast at the first return
    // to Idle after a live recording epoch - the boundary BEFORE session B can start. These tests
    // drive the state machine directly through a fake INotifyPropertyChanged source (no WPF window,
    // no async controller), which is why the Finalizing hole below is now provable in a unit test.

    /// <summary>Tiny stand-in for SessionViewModel's State surface: a mutable state plus a raise
    /// that mirrors CommunityToolkit's [ObservableProperty] notification (fires "State").</summary>
    private sealed class FakeStateSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public SessionState State { get; private set; } = SessionState.Idle;

        public FakeStateSource(SessionState initial) => State = initial;

        /// <summary>Transition to a new state and raise PropertyChanged("State"), exactly as the
        /// generated SessionViewModel.State setter does when the controller reports a new state.</summary>
        public void Set(SessionState next)
        {
            State = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    // --- The exact shipped defect: a toast raised during FINALIZING must close before session B. ---

    [Fact]
    public void Finalizing_raised_toast_closes_at_Idle_and_not_again_for_session_B()
    {
        // Clicking Stop moves Recording -> Finalizing; the next call-end tick fires CallEndAdvised
        // while State == Finalizing (State != Idle, the advisor still armed). ShowStopConfirmToast
        // raises the toast HERE. The old inline predicate (Recording/Paused only) installed no guard
        // and the toast lingered past Idle into a fresh recording. The guard must catch this.
        var source = new FakeStateSource(SessionState.Finalizing);
        var closes = 0;
        using var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        Assert.Equal(0, closes);            // still finalizing - toast stays up

        source.Set(SessionState.Idle);      // finalize drain completes -> app is Idle
        Assert.Equal(1, closes);            // toast auto-closed at the Idle boundary, exactly once

        // Session B begins. The toast is already gone, so B can never be stopped by it. Proving the
        // guard does NOT re-fire on the later Recording epoch is proving the toast closed before B.
        source.Set(SessionState.Recording);
        Assert.Equal(1, closes);            // no second close: the toast is long gone before B
    }

    // --- A Recording-raised toast (the normal call-end advisory) closes only at Idle. ---

    [Fact]
    public void Recording_raised_toast_closes_at_Idle()
    {
        var source = new FakeStateSource(SessionState.Recording);
        var closes = 0;
        using var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        source.Set(SessionState.Finalizing);   // Stop drain: still a live epoch
        Assert.Equal(0, closes);

        source.Set(SessionState.Idle);
        Assert.Equal(1, closes);
    }

    [Fact]
    public void Pause_and_resume_within_the_same_epoch_do_not_close_the_toast()
    {
        // Paused is still the SAME recording epoch (the session the toast was raised for); the toast
        // must survive a mid-recording pause/resume and only close when the recording truly ends.
        var source = new FakeStateSource(SessionState.Recording);
        var closes = 0;
        using var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        source.Set(SessionState.Paused);
        source.Set(SessionState.Recording);    // resumed - same epoch throughout
        Assert.Equal(0, closes);

        source.Set(SessionState.Idle);         // now the recording ends
        Assert.Equal(1, closes);
    }

    // --- Dispose (user click / auto-dismiss) unsubscribes without firing. ---

    [Fact]
    public void Dispose_before_Idle_never_fires_and_unsubscribes()
    {
        // The toast's Closed handler disposes the guard (user clicked a button, or it auto-dismissed
        // while still recording). onEpochEnded must NOT fire, and no later Idle may reach it - the
        // handler is detached from the app-lifetime SessionViewModel (no leak, no fire-against-B).
        var source = new FakeStateSource(SessionState.Recording);
        var closes = 0;
        var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        guard.Dispose();
        Assert.Equal(0, closes);

        source.Set(SessionState.Idle);         // the epoch ends AFTER the toast was already gone
        Assert.Equal(0, closes);               // detached: never fires
        source.Set(SessionState.Recording);    // session B
        Assert.Equal(0, closes);
    }

    [Fact]
    public void Dispose_after_firing_is_a_no_op()
    {
        var source = new FakeStateSource(SessionState.Recording);
        var closes = 0;
        var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        source.Set(SessionState.Idle);
        Assert.Equal(1, closes);

        guard.Dispose();                       // toast already closed by the guard itself
        Assert.Equal(1, closes);
        guard.Dispose();                       // idempotent
        Assert.Equal(1, closes);
    }

    // --- Idempotency + safety edges. ---

    [Fact]
    public void Multiple_Idle_notifications_fire_onEpochEnded_at_most_once()
    {
        var source = new FakeStateSource(SessionState.Recording);
        var closes = 0;
        using var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        source.Set(SessionState.Idle);
        source.Set(SessionState.Idle);         // a redundant repeat notification
        Assert.Equal(1, closes);
    }

    [Fact]
    public void Constructed_while_already_Idle_never_fires()
    {
        // Defensive: a stop-confirm toast is only ever raised for a live recording, but if somehow
        // constructed at Idle there is no epoch to protect (the Stop button is already inert). The
        // guard must be inert too - never fire, no NRE, Dispose safe.
        var source = new FakeStateSource(SessionState.Idle);
        var closes = 0;
        var guard = new StopConfirmToastGuard(source, () => source.State, () => closes++);

        source.Set(SessionState.Recording);    // a brand-new recording after the no-op toast
        source.Set(SessionState.Idle);
        Assert.Equal(0, closes);               // never bound to that later epoch

        guard.Dispose();                       // safe no-op
        Assert.Equal(0, closes);
    }
}

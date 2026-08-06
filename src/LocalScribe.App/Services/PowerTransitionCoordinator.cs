using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
namespace LocalScribe.App.Services;

/// <summary>Suspend/resume policy for a live recording (Tier 1B design 2026-08-05, T1-4d).
///
/// THE PROBLEM: closing a laptop lid mid-call leaves capture running into a suspended audio stack.
/// Nothing in the solution subscribed SystemEvents at all, and Markers.PausedSystemSleep has been
/// declared since Stage 2b with no writer. On wake the session simply carries on with an
/// unexplained hole - and because the session clock is MONOTONIC (StopwatchClock/QPC), it does not
/// advance across the suspend, so even the hole's size is invisible from Core.
///
/// Extracted rather than written inline in the PowerModeChanged handler for the StopConfirmToastGuard
/// reason: App.xaml.cs has no test coverage in this repo, so anything decided there is decided
/// untested. TimeProvider is injected because the wall-clock gap is the entire deliverable.
///
/// Only ever undoes ITS OWN pause. A user who paused for a privileged aside and then closed the lid
/// must not come back to a recording session - that is an evidentiary violation, not a convenience.</summary>
public sealed class PowerTransitionCoordinator(
    Func<SessionState> state,
    Func<Task> pauseForSleep,
    Func<TimeSpan, Task> resumeAfterSleep,
    TimeProvider time,
    Action<string> notify,
    IDiagnosticLog? log = null)
{
    private DateTimeOffset? _suspendedAtUtc;

    /// <summary>True while a pause THIS coordinator performed is outstanding.</summary>
    public bool AutoPaused => _suspendedAtUtc is not null;

    /// <summary>The whole PowerModeChanged decision, so App.xaml.cs is left with one delegating
    /// line. The branch lives HERE because App.xaml.cs has no test coverage anywhere in this repo
    /// (105 test files, no AppTests.cs) - a branch written into that lambda is a branch nothing ever
    /// exercises. Only Suspend and Resume matter; PowerModes.StatusChange (a battery/AC transition)
    /// is deliberately ignored, and the caller passes only the two it cares about.</summary>
    public Task OnPowerModeAsync(bool suspending)
        => suspending ? OnSuspendAsync() : OnResumeAsync();

    /// <summary>The machine is suspending. Never throws: it runs from a SystemEvents callback
    /// during a suspend, where an escaping exception is unhandled on a thread nobody is watching at
    /// the worst possible moment.</summary>
    public async Task OnSuspendAsync()
    {
        if (state() != SessionState.Recording) return;   // Paused/Idle/Finalizing: nothing to protect
        var at = time.GetUtcNow();
        try
        {
            log?.Write("info", "session", "System suspending - pausing the recording");
            await pauseForSleep();
            _suspendedAtUtc = at;                        // set only on SUCCESS: a failed pause must
                                                         // never be "resumed" later
        }
        catch (Exception ex)
        {
            log?.Write("error", "session", "Pause on suspend failed", DiagnosticRedaction.ForException(ex));
            notify("Could not pause the recording before the machine slept: " + ex.Message);
        }
    }

    /// <summary>The machine has woken. A no-op unless THIS coordinator paused. Never throws.</summary>
    public async Task OnResumeAsync()
    {
        if (_suspendedAtUtc is not { } at) return;
        // Cleared FIRST: Windows can raise Resume more than once for one suspend (Resume and
        // ResumeAutomatic), and a second pass must not report a second, wrong gap.
        _suspendedAtUtc = null;
        var gap = time.GetUtcNow() - at;
        if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;    // an NTP correction must never read as negative
        try
        {
            log?.Write("info", "session", "System resumed - resuming the recording",
                $"gapSeconds={(long)gap.TotalSeconds}");
            await resumeAfterSleep(gap);
        }
        catch (Exception ex)
        {
            log?.Write("error", "session", "Resume after sleep failed", DiagnosticRedaction.ForException(ex));
            notify("Could not resume the recording after the machine woke: " + ex.Message
                + " Use Resume on the record console.");
        }
    }
}

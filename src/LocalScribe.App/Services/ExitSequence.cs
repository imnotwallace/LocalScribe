using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
namespace LocalScribe.App.Services;

/// <summary>The one stop-then-drain-then-exit sequence, shared by the tray Exit menu item and
/// Application.SessionEnding (Tier 1B design 2026-08-05, T1-2).
///
/// THE BUG IT CLOSES: StopAsync finalizes audio synchronously, hands the transcript drain AND the
/// session.json EndedAtUtc/DurationMs write to a background task, flips to Idle and returns. Tray
/// Exit awaited StopCommand.ExecutionTask - which IS StopAsync - and then called Shutdown(),
/// abandoning that write; the Idle branch awaited nothing at all. The result is a never-ended
/// session that goes through crash recovery on the next launch.
///
/// WPF-free and extracted rather than inlined: TrayIconHost has no test coverage in this repo (no
/// TrayIconHostTests.cs, no STA harness), so anything left in that class is permanently untestable -
/// the StopConfirmToastGuard precedent. The MessageBox and the Shutdown() call stay at the call
/// site; every decision lives here.</summary>
public sealed class ExitSequence(
    Func<SessionState> state,
    Func<Task> stopRecording,
    Func<Task?> inFlightStop,
    Func<Task> drainFinalize,
    Func<bool> confirmStopWhileRecording,
    Action<string> notify,
    Func<Task>? flushDiagnostics = null,
    IDiagnosticLog? log = null,
    TimeSpan? shutdownBudget = null)
{
    /// <summary>How long a caller that CANNOT block indefinitely may wait on this sequence -
    /// Application.SessionEnding, where the OS is waiting on the UI thread. Lives here rather than
    /// as a literal in an App.xaml.cs lambda because App.xaml.cs has no test coverage in this repo
    /// (105 test files, no AppTests.cs), so a number left there is a number nothing asserts.
    /// REJECTED: an unbounded wait - a hung drain would hold up the machine's logoff, which is
    /// hostile and ends in the app being killed regardless.
    ///
    /// This is the SEQUENCE budget (stop + finalize drain + flush), and it is NOT the ceiling the
    /// diagnostic flush inside RunCoreAsync bounds itself with. That leg uses
    /// ShutdownFlush.Timeout (LocalScribe.App.Services, 2s), which Plan A created as the ONE
    /// constant both exit-path flushes share - App.OnExit's blocking backstop and TrayIconHost's
    /// Exit-menu await. Before it existed each site carried its own literal and the two had already
    /// drifted once. Keep the two ceilings distinct and cross-referenced so they cannot drift
    /// again.</summary>
    public TimeSpan ShutdownBudget { get; } = shutdownBudget ?? TimeSpan.FromSeconds(8);

    /// <summary>Runs the sequence with the user present. Returns true when the caller may proceed
    /// to Shutdown(), false only when the user declined the "a recording is in progress" prompt.
    /// Never throws.</summary>
    public Task<bool> RunAsync() => RunCoreAsync(confirm: true);

    /// <summary>The SAME sequence with the confirm prompt SKIPPED - for Application.SessionEnding
    /// (Windows logging off or shutting down). NOBODY CAN ANSWER A MODAL BOX during logoff: the OS
    /// is tearing the session down and the caller can only wait ShutdownBudget, so a prompt there
    /// expires with stopRecording never called and a live evidentiary session orphaned with no
    /// EndedAtUtc - the exact loss this whole task exists to close. Windows has already asked the
    /// user whether to log off, so a second question would be redundant even if it could be seen.
    /// REJECTED: a second hand-written unattended sequence - the confirm is the ONLY difference,
    /// and two copies of an evidentiary shutdown path drift.</summary>
    public Task<bool> RunUnattendedAsync() => RunCoreAsync(confirm: false);

    private async Task<bool> RunCoreAsync(bool confirm)
    {
        try
        {
            var s = state();
            if (s is SessionState.Recording or SessionState.Paused)
            {
                // Attended only: never kill a live recording silently while the user is there to
                // be asked. Unattended, stopping IS the protective act.
                if (confirm && !confirmStopWhileRecording()) return false;
                log?.Write("info", "session", "Exit requested while recording - stopping first",
                    $"confirmed={confirm}");
                await stopRecording();
            }
            else if (s == SessionState.Finalizing)
            {
                // A stop is already in flight (Exit clicked right after Stop): do not re-confirm.
                if (inFlightStop() is { } finalize) await finalize;
            }
        }
        catch (Exception ex)
        {
            // A StopAsync fault must not become an unhandled async-void exception, and must not
            // block the exit the user already asked for.
            log?.Write("error", "session", "Stop failed on the exit path", DiagnosticRedaction.ForException(ex));
            notify("Error stopping recording: " + ex.Message);
        }

        // DELIBERATELY OUTSIDE the try/catch above, and deliberately unconditional.
        // - Outside, because a faulted stop must still reach this line.
        // - Unconditional, because the Idle branch is the common case: a Stop seconds ago has
        //   already returned Idle while its background finalize is still writing session.json.
        // - AFTER the stop, never before: StopAsync assigns _pendingFinalize synchronously before
        //   returning, so awaiting first would await the PREVIOUS session's completed task.
        // The delegate re-reads SessionController.PendingFinalize on every call - it is a property
        // over a reassigned field, so a captured Task would be permanently stale.
        // KNOWN LIMITATION, stated rather than papered over: on the StopAsync FAULT path
        // _pendingFinalize is never assigned at all, so this await returns instantly with
        // session.json unwritten. The launch-time recovery scan is the documented safety net for
        // that path (SessionController's own FinalizeInBackgroundAsync catch says so), and Task 1
        // of this plan is what makes that recovery non-lossy.
        try { await drainFinalize(); }
        catch { /* FinalizeInBackgroundAsync swallows everything; this can only be a wiring fault */ }

        // LAST, and only on a path that is genuinely exiting. The shared contract (section 1) names
        // this as one of IDiagnosticLog.FlushAsync's two mandated call sites - "Awaited by
        // App.OnExit and by the tray Exit path" - and this class IS the tray Exit path now, as well
        // as the SessionEnding path. After the drain, never before: the stop, the fault notice and
        // the drain all write diagnostics, so flushing earlier would persist a log that stops short
        // of the shutdown it exists to explain. Null-safe, so an ExitSequence built with no log
        // (every unit test that does not care) still runs.
        //
        // BOUNDED, never a bare await (SHARED-CONTRACT section 1b). FlushAsync's CancellationToken
        // is accepted for call-site symmetry and deliberately NOT honoured, so the caller is the
        // only place a ceiling can live. Plan A shipped this exact await unbounded on the tray Exit
        // path in round 1 and had to fix it: against a wedged drain (dead disk, vanished network
        // path, antivirus holding the file) the line never completes, so Shutdown() never runs, so
        // OnExit's backstop never runs either, and the user is left with a tray process only Task
        // Manager can end. ShutdownFlush.Timeout (2s, LocalScribe.App.Services) is the ONE ceiling
        // both exit-path flushes share - deliberately NOT ShutdownBudget, which covers the whole
        // stop-plus-finalize sequence and is a different thing.
        //
        // F14 (Plan A's final whole-branch review, carried here with the code it explains): ONE
        // SHARED CONSTANT, NOT ONE SHARED CEILING. The two waits are ADDITIVE on the tray path -
        // this line waits up to ShutdownFlush.Timeout, then the caller's Shutdown() runs
        // App.OnExit, which waits up to ShutdownFlush.Timeout AGAIN on the same still-wedged
        // chain. With a dead network storage root, tray Exit takes 2 s + 2 s = 4 s, not 2 s. That
        // is ACCEPTED and deliberately not changed: both bounds are needed independently (OnExit
        // is the backstop for every OTHER route into shutdown, which never passes through here),
        // the worst case is bounded and small, and it only occurs when the disk is already gone.
        // Nothing here should be "optimised" by dropping one of them.
        try
        {
            Task flush = flushDiagnostics?.Invoke() ?? Task.CompletedTask;
            await Task.WhenAny(flush, Task.Delay(ShutdownFlush.Timeout));
        }
        catch { /* FlushAsync never throws by contract; a wiring fault must not block the exit */ }
        return true;
    }
}

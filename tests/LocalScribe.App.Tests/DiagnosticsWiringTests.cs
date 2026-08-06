using System;
using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Source-text pins for the diagnostics wiring in App.xaml.cs and TrayIconHost.cs (Tier 1
/// plan A, 2026-08-05). Those two files have NO unit coverage at all - 105 test files, no
/// AppTests.cs, no TrayIconHostTests.cs - and every policy this round adds is already extracted
/// into a WPF-free tested class. What is left is one-line wiring, and a text assertion is the only
/// guard available for it; XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj asserts on raw csproj
/// text the same way. If one of these fails after a refactor, re-point the pin - do not delete it
/// and do not delete the wiring.</summary>
public sealed class DiagnosticsWiringTests
{
    private static string App() => File.ReadAllText(RepoPaths.AppXaml("App.xaml.cs"));
    private static string CompositionRootSource() => File.ReadAllText(RepoPaths.AppXaml("CompositionRoot.cs"));
    private static string Tray() => File.ReadAllText(RepoPaths.AppXaml("TrayIconHost.cs"));
    /// <summary>Tier 1B (2026-08-06): the tray Exit path's decision logic - including the bounded
    /// diagnostic flush Plan A pinned here - moved into ExitSequence, which unlike TrayIconHost has
    /// real unit tests. Re-pointed, not deleted, exactly as this class's doc instructs.</summary>
    private static string ExitSequenceSource() => File.ReadAllText(RepoPaths.AppXaml(Path.Combine("Services", "ExitSequence.cs")));

    [Fact]
    public void The_build_stamp_header_is_written_only_after_the_first_run_consent_check()
    {
        string app = App();
        Assert.Contains("_log = comp.Log;", app);
        Assert.Contains("\"LocalScribe started\"", app);
        Assert.Contains("\"build=\" + comp.BuildInfo", app);

        // F1 (final whole-branch review, 2026-08-05): the header Write used to sit immediately
        // under `_log = comp.Log;`, ABOVE the first-run consent block - whose own comment promises
        // "Decline (or dismissing the dialog) shuts the app down without persisting anything".
        // Write() enqueues and kicks a drain that does Directory.CreateDirectory(DiagnosticsDir)
        // and appends, and OnExit's bounded FlushAsync then deterministically forces it to land, so
        // on a fresh install a Decline left {StorageRoot}\diagnostics\diag-YYYYMM.jsonl behind on a
        // machine where consent was refused. The ASSIGNMENT must stay above the consent block (it
        // is what lets OnExit flush on every exit route, including Decline); only the WRITE moved.
        // ORDER, not presence, is the entire content of this pin - re-ordering these three lines is
        // exactly the regression, and every "is it still there" assertion above stays green for it.
        int assign = app.IndexOf("_log = comp.Log;", StringComparison.Ordinal);
        int consent = app.IndexOf("if (comp.Settings.Current.ConsentNotice is null)", StringComparison.Ordinal);
        int header = app.IndexOf("\"LocalScribe started\"", StringComparison.Ordinal);
        Assert.True(assign > 0, "App.xaml.cs must still capture the log sink into the _log field");
        Assert.True(consent > assign,
            "the log sink must be captured BEFORE the consent check, so OnExit can flush a Decline");
        Assert.True(header > consent,
            "the 'LocalScribe started' header must be written AFTER the first-run consent check - "
            + "writing it above the modal persists a diagnostics file on a Decline");
    }

    [Fact]
    public void Dispatcher_exceptions_are_recorded_not_swallowed()
    {
        string app = App();
        Assert.Contains("ex.Handled = _recorder?.Handle(ex.Exception) ?? true;", app);
        // The line this round exists to delete. Its comment said "Stage 7 can add real logging
        // here; for now, swallow it" - this IS that round.
        Assert.DoesNotContain("DispatcherUnhandledException += (_, ex) => { ex.Handled = true; };", app);
        // ONE error line per dispatcher exception. notify enqueues straight onto the InfoBar queue
        // rather than calling errors.Report(...), which after Task 7 has its own log sink and would
        // write a SECOND error entry at source "ui" - and steal LastError from the dispatcher line.
        Assert.Contains("errors.Messages.Add(\"Unexpected error: \" + ex.Message)", app);
        Assert.DoesNotContain("errors.Report(\"Unexpected error\"", app);
    }

    [Fact]
    public void The_session_recorder_is_subscribed_to_all_four_controller_events()
    {
        string app = App();
        Assert.Contains("comp.Controller.StateChanged += sessionDiag.StateChanged;", app);
        Assert.Contains("comp.Controller.ErrorRaised += sessionDiag.ErrorRaised;", app);
        Assert.Contains("comp.Controller.Notice += sessionDiag.Notice;", app);
        Assert.Contains("comp.Controller.SessionFinalizeCompleted += sessionDiag.FinalizeCompleted;", app);
    }

    [Fact]
    public void ExternalEngineBusy_marks_the_session_id_before_it_reaches_SessionController_Notice()
    {
        // Fix round 2 (2026-08-05, Important finding): CompositionRootTests'
        // ExternalEngineBusy_notice_stays_plain_on_screen_and_is_redacted_on_disk drives a COPY of
        // this expression written inside the test - CompositionRoot.cs itself is never loaded, so
        // deleting DiagnosticRedaction.Mark(rid) from CompositionRoot.cs leaves every test green
        // and the leak returns silently. This is the source-text pin that actually reads the file:
        // App.xaml.cs/TrayIconHost.cs have no unit coverage (see the class doc above), and this one
        // line of CompositionRoot.cs is in the same boat - a Func<string?> assigned inline, never
        // called from a seam a test can drive without also standing up a real RetranscriptionRunner
        // with a genuinely "running" re-transcription.
        Assert.Contains("DiagnosticRedaction.Mark(rid)", CompositionRootSource());
    }

    [Fact]
    public void Settings_receives_the_build_stamp_and_the_live_last_error()
    {
        // Fix round 1 (2026-08-05, coordinator IMPORTANT finding 1): App.xaml.cs has NO unit
        // coverage (see the class doc above). Deleting these two Task 11 arguments leaves every
        // other test green - SettingsPageViewModelTests only ever injects fakes for buildInfo/
        // lastError - while the shipped app silently degrades to "LocalScribe (development
        // build)" and a "Copy last error" button that reports "No errors" after every crash, on
        // the one surface whose entire purpose is production diagnosability.
        string app = App();
        Assert.Contains("buildInfo: comp.BuildInfo", app);
        Assert.Contains("lastError: () => comp.Log.LastError", app);
    }

    [Fact]
    public void OnExit_drains_the_diagnostic_queue_with_a_bounded_wait()
    {
        string app = App();
        // Fix round 1 (2026-08-05): re-pointed from a hardcoded `TimeSpan.FromSeconds(2)` literal
        // to the ShutdownFlush.Timeout constant the tray Exit path bounds its own wait with too
        // (see The_tray_exit_flush_is_bounded_not_unbounded below) - one shared ceiling, not two
        // literals that can silently drift apart the way the tray's already had.
        Assert.Contains("_log?.FlushAsync(CancellationToken.None).Wait(ShutdownFlush.Timeout)", app);
    }

    [Fact]
    public void The_tray_exit_awaits_the_flush_before_shutting_down()
    {
        string tray = Tray();
        int flush = tray.IndexOf("_log?.FlushAsync", StringComparison.Ordinal);
        int shutdown = tray.IndexOf("Application.Current.Shutdown();", StringComparison.Ordinal);
        Assert.True(flush > 0, "the tray Exit handler must flush the diagnostic log");
        Assert.True(shutdown > flush, "the flush must be awaited BEFORE Shutdown()");
    }

    [Fact]
    public void The_tray_exit_flush_is_bounded_not_unbounded()
    {
        // Tier 1B (2026-08-06): RE-POINTED from TrayIconHost.cs to ExitSequence.cs. The flush leg
        // moved there wholesale when the tray Exit handler became glue over the tested sequence;
        // the bound moved WITH it and this pin follows the code rather than being weakened. The
        // sequence is now shared with Application.SessionEnding, so the bound this asserts protects
        // the logoff path too, not just the menu item.
        string tray = ExitSequenceSource();
        // Fix round 1 (2026-08-05): round 1 shipped this line as
        // `await (_log?.FlushAsync(CancellationToken.None) ?? Task.CompletedTask);` with NO bound.
        // A wedged drain (dead disk, vanished network path, antivirus holding the file) would hang
        // that line forever, so Application.Current.Shutdown() on the next line would never run -
        // the app's only Exit menu item would leave a tray process only Task Manager can end.
        // Task.WhenAny against a Task.Delay(ShutdownFlush.Timeout) bounds the wait regardless of
        // whether FlushAsync's CancellationToken is ever honoured.
        Assert.Contains("Task.WhenAny(flush, Task.Delay(ShutdownFlush.Timeout))", tray);
        Assert.DoesNotContain(
            "await (_log?.FlushAsync(CancellationToken.None) ?? Task.CompletedTask);", tray);
    }

    // ---------------------------------------------------------------------------------------
    // F2 (final whole-branch review, 2026-08-05). Seven wiring lines had NO pin anywhere in
    // tests/, and FIVE of them pass an OPTIONAL parameter - so deleting the argument COMPILES and
    // leaves the whole solution green (Core 1220, App 1025, Mcp 6) while the shipped app silently
    // stops recording that subsystem. That is verbatim the defect Task 8's fix round 2 already
    // identified for CompositionRoot.cs's Mark(rid) ("a HAND-WRITTEN expression that MIRRORS
    // CompositionRoot.cs's shape, not a load of CompositionRoot.cs itself") and then did not
    // generalise. The existing VM/unit tests prove the POLICY of each of these classes; only a
    // source-text pin proves the WIRING. Each fact asserts the shipped form PRESENT and the
    // degraded form ABSENT - the idiom that is why this round caught what it caught.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_InfoBar_reporter_is_given_the_diagnostic_log()
    {
        string app = App();
        // Delete the second argument and every UI Report/Info stops logging: the source "ui"
        // disappears from diag-*.jsonl entirely, and InfoBarErrorReporter's log param is optional
        // (defaulted null "so every existing test keeps building"), so nothing fails.
        Assert.Contains("new InfoBarErrorReporter(dispatch, comp.Log)", app);
        Assert.DoesNotContain("new InfoBarErrorReporter(dispatch)", app);
    }

    [Fact]
    public void The_dispatcher_recorder_logs_the_exception_at_the_dispatcher_source()
    {
        string app = App();
        // UnhandledExceptionRecorder's log param is REQUIRED, so this one cannot be deleted - it
        // can only be NEUTERED to a no-op lambda, which is the form pinned absent below. Without
        // the real sink the dispatcher swallow this whole round exists to delete is back, silently.
        Assert.Contains("log: ex => comp.Log.Write(", app);
        Assert.Contains("\"dispatcher\", \"Unhandled dispatcher exception\"", app);
        Assert.Contains("DiagnosticRedaction.ForException(ex)", app);
        Assert.DoesNotContain("log: _ => { }", app);
        // The source tag itself: "dispatcher" is what separates this line from the "ui" entry
        // InfoBarErrorReporter.Report would write, and the comment above the wiring turns on that
        // distinction (one exception, ONE line, at source "dispatcher").
        Assert.Contains("\"dispatcher\"", app);
    }

    [Fact]
    public void The_copy_last_error_button_is_given_a_real_clipboard()
    {
        string app = App();
        // SettingsPageViewModel falls back to `_copyToClipboard = copyToClipboard ?? (_ => { })`,
        // so deleting this optional argument turns "Copy last error" into a dead button that
        // reports success and copies nothing - on the one surface whose entire purpose is handing
        // a failure to support.
        Assert.Contains("copyToClipboard: text => Clipboard.SetText(text)", app);
        Assert.DoesNotContain("copyToClipboard: _ => { }", app);
        Assert.DoesNotContain("copyToClipboard: null", app);
    }

    [Fact]
    public void The_tray_host_is_given_the_log_it_flushes_at_exit()
    {
        string app = App();
        // TrayIconHost's log param is optional; without it _log is null, so the tray Exit flush
        // degrades to `Task.CompletedTask` and the entire bounded-exit-flush machinery - pinned in
        // detail by the two tray facts above - becomes inert while every one of those pins stays
        // green, because they only read TrayIconHost.cs.
        // Tier 1B (2026-08-06): was `log: comp.Log);` - the trailing paren was load-bearing in the
        // old text and is gone, because `drainFinalize:` now follows this argument. Pinning the
        // argument WITHOUT the paren keeps the wiring pinned while letting further arguments be
        // appended, which is what a construction site with optional parameters invites.
        Assert.Contains("log: comp.Log", app);
        Assert.DoesNotContain("log: null", app);
        int tray = app.IndexOf("_tray = new TrayIconHost(", StringComparison.Ordinal);
        int log = app.IndexOf("log: comp.Log", StringComparison.Ordinal);
        Assert.True(tray > 0, "App.xaml.cs must still construct the TrayIconHost");
        Assert.True(log > tray && log - tray < 1200,
            "log: comp.Log must be an argument of the TrayIconHost construction, not a stray line");
    }

    [Fact]
    public void The_tray_host_is_given_the_finalize_drain_it_awaits_at_exit()
    {
        // Tier 1B (2026-08-05, T1-2). drainFinalize is OPTIONAL, so deleting this argument compiles
        // and leaves every other test green - including all nine ExitSequenceTests facts, which
        // drive the sequence over their own delegates and can never observe the production wiring -
        // while the shipped app silently returns to abandoning session.json on every ordinary exit
        // taken seconds after Stop. That is the whole defect T1-2 exists to close, so it gets the
        // same present-AND-absent pin as the log argument above.
        string app = App();
        Assert.Contains("drainFinalize: () => comp.Controller.PendingFinalize", app);
        int tray = app.IndexOf("_tray = new TrayIconHost(", StringComparison.Ordinal);
        int drain = app.IndexOf("drainFinalize: () => comp.Controller.PendingFinalize", StringComparison.Ordinal);
        Assert.True(drain > tray && drain - tray < 1600,
            "drainFinalize must be an argument of the TrayIconHost construction, not a stray line");

        // A CAPTURED task would be permanently stale: PendingFinalize is a property over a field
        // StopAsync reassigns per session, so `drainFinalize: comp.Controller.PendingFinalize`
        // (no lambda) would await the PREVIOUS session's already-completed task forever.
        Assert.DoesNotContain("drainFinalize: comp.Controller.PendingFinalize)", app);
    }

    [Fact]
    public void The_startup_tray_reporter_is_given_the_diagnostic_log()
    {
        string app = App();
        // TrayNoticeReporter's own doc: Focus Assist suppresses tray balloons outright, so for a
        // recovery failure the LOG LINE can be the only record that survives. Optional param again.
        Assert.Contains("new TrayNoticeReporter(notify, comp.Log)", app);
        Assert.DoesNotContain("new TrayNoticeReporter(notify)", app);
    }

    [Fact]
    public void The_session_id_probe_covers_the_finalize_window_too()
    {
        string app = App();
        // Drop the ?? half and CurrentSessionId is already null by the time the finalize drain
        // runs, so every finalize-time line silently reads "session=(none)" - a green suite and a
        // support file that cannot say which session failed.
        Assert.Contains(
            "() => comp.Controller.CurrentSessionId ?? comp.Controller.FinalizingSessionId", app);
        Assert.DoesNotContain("() => comp.Controller.CurrentSessionId)", app);
    }

    [Fact]
    public void The_capture_provider_is_wired_to_the_diagnostic_log()
    {
        string root = CompositionRootSource();
        // Optional param: delete it and every capture diagnostic - activation fallback, device
        // invalidated, data discontinuity - goes nowhere, exactly as it did before this round.
        // The LEVEL is chosen by CompositionRoot.CaptureDiagnosticLevel (F3); that mapping has its
        // own behavioural tests, this pin only guards that the sink is attached at all.
        Assert.Contains("diagnostic: m => log.Write(CaptureDiagnosticLevel(m), \"capture\", m)", root);
        Assert.DoesNotContain(
            "new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator)", root);
    }

    [Fact]
    public void The_diarisation_helper_is_wired_to_the_diagnostic_log()
    {
        string root = CompositionRootSource();
        // Optional param: delete it and helper exit codes are never logged, so a crashed diarizer
        // survives only as a dialog the user has already dismissed by the time they ask for help -
        // which is the exact gap SherpaHelperDiariser's own logging was added to close.
        Assert.Contains("new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe), log)", root);
        Assert.DoesNotContain("new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe))", root);
    }
}

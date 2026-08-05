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

    [Fact]
    public void Startup_records_the_build_stamp_as_the_first_diagnostic_line()
    {
        string app = App();
        Assert.Contains("_log = comp.Log;", app);
        Assert.Contains("\"LocalScribe started\"", app);
        Assert.Contains("\"build=\" + comp.BuildInfo", app);
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
    public void OnExit_drains_the_diagnostic_queue_with_a_bounded_wait()
    {
        string app = App();
        Assert.Contains("_log?.FlushAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(2))", app);
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
}

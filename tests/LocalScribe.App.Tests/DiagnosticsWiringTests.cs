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
}

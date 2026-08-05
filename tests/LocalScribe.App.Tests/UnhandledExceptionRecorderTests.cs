using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The record-and-notify policy behind DispatcherUnhandledException (Tier 1 plan A,
/// 2026-08-05), extracted WPF-free so it can be tested at all - App.xaml.cs has no test coverage,
/// and every tested App-layer service is an extracted class (the StopConfirmToastGuard precedent,
/// rationale recorded at App.xaml.cs:864-874).</summary>
public sealed class UnhandledExceptionRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-unhandled-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Handle_logs_then_notifies_with_the_same_exception_and_marks_it_handled()
    {
        var logged = new List<Exception>();
        var notified = new List<Exception>();
        var recorder = new UnhandledExceptionRecorder(logged.Add, notified.Add);
        var boom = new InvalidOperationException("stop faulted");

        Assert.True(recorder.Handle(boom));
        Assert.Same(boom, Assert.Single(logged));
        Assert.Same(boom, Assert.Single(notified));
    }

    [Fact]
    public void A_throwing_log_still_notifies_and_still_returns_true()
    {
        var notified = new List<Exception>();
        var recorder = new UnhandledExceptionRecorder(
            _ => throw new IOException("disk full"), notified.Add);

        // Each side is independently guarded: a failing LOG must not cost the user the NOTICE.
        Assert.True(recorder.Handle(new InvalidOperationException("x")));
        Assert.Single(notified);
    }

    [Fact]
    public void A_throwing_notify_still_returns_true_after_the_log_ran()
    {
        var logged = new List<Exception>();
        var recorder = new UnhandledExceptionRecorder(
            logged.Add, _ => throw new InvalidOperationException("no window yet"));

        Assert.True(recorder.Handle(new InvalidOperationException("x")));
        Assert.Single(logged);
    }

    [Fact]
    public void Both_sides_throwing_still_returns_true()
    {
        // The value returned here becomes DispatcherUnhandledExceptionEventArgs.Handled. Returning
        // false - even once, even on the "logging itself is broken" path - lets an unhandled
        // AsyncRelayCommand fault kill the whole tray app, and that crash can land MID-RECORDING.
        var recorder = new UnhandledExceptionRecorder(
            _ => throw new IOException("disk full"), _ => throw new InvalidOperationException("no ui"));
        Assert.True(recorder.Handle(new InvalidOperationException("x")));
    }

    [Fact]
    public async Task A_dispatcher_exception_leaves_exactly_one_error_line_and_it_names_the_dispatcher()
    {
        // The cross-task seam Tasks 6 and 7 create together, and the reason App.xaml.cs's notify
        // lambda enqueues instead of calling errors.Report(...): this Step gives
        // InfoBarErrorReporter its OWN log sink, so a Report on the dispatcher path would write a
        // SECOND error entry at source "ui" - and DiagnosticLog latches LastError on every
        // error-level entry, so the "ui" line would win and Settings' "Copy last error" would hand
        // support the less specific of the two. Drives the REAL classes, wired exactly as
        // App.xaml.cs wires them. If it fails, re-read that lambda - do not relax the assertion.
        var paths = new StoragePaths(_root);
        var log = new DiagnosticLog(paths, new ManualUtcTimeProvider(
            new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)), () => new LoggingSetting());
        var errors = new InfoBarErrorReporter(a => a(), log);
        var recorder = new UnhandledExceptionRecorder(
            log: ex => log.Write(DiagnosticLevels.Error, "dispatcher",
                "Unhandled dispatcher exception", DiagnosticRedaction.ForException(ex)),
            notify: ex => errors.Messages.Add("Unexpected error: " + ex.Message));

        Assert.True(recorder.Handle(new InvalidOperationException("stop faulted")));
        await log.FlushAsync(default);

        Assert.Equal("dispatcher", log.LastError!.Source);
        Assert.Equal("Unhandled dispatcher exception", log.LastError!.Message);
        string[] lines = await File.ReadAllLinesAsync(
            Path.Combine(paths.DiagnosticsDir, "diag-202608.jsonl"));
        Assert.Single(lines);                                  // ONE line, not two
        // ...and the user still sees the exact string Report would have produced.
        Assert.Equal(new[] { "Unexpected error: stop faulted" }, errors.Messages);
    }
}

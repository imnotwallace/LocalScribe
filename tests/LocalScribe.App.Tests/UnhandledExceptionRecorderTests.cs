using System.IO;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The record-and-notify policy behind DispatcherUnhandledException (Tier 1 plan A,
/// 2026-08-05), extracted WPF-free so it can be tested at all - App.xaml.cs has no test coverage,
/// and every tested App-layer service is an extracted class (the StopConfirmToastGuard precedent,
/// rationale recorded at App.xaml.cs:864-874).</summary>
public sealed class UnhandledExceptionRecorderTests
{
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
}

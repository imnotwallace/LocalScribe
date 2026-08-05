namespace LocalScribe.App.Services;

/// <summary>Records a dispatcher-unhandled exception and notifies the user, replacing the
/// swallow-everything handler that stood at App.xaml.cs:50-55 since Stage 3 (Tier 1 plan A,
/// 2026-08-05, spec item T1-1). Handle() returns the value to assign to
/// DispatcherUnhandledExceptionEventArgs.Handled and MUST return true on EVERY path - including
/// when logging or reporting themselves throw - because the original comment is still true: an
/// unhandled AsyncRelayCommand fault (AwaitAndThrowIfFailed rethrows a faulted Stop/Pause command
/// on the dispatcher) kills the whole tray app, and that crash can land mid-recording.
///
/// Delegate-injected and WPF-free so it is testable: App.xaml.cs itself has no test coverage at
/// all, and every tested App-layer service is an extracted class - the StopConfirmToastGuard
/// precedent, whose extraction rationale is recorded at App.xaml.cs:910-918.</summary>
public sealed class UnhandledExceptionRecorder(Action<Exception> log, Action<Exception> notify)
{
    public bool Handle(Exception ex)
    {
        // TWO independent try blocks, not one around both: a failing log must not cost the user
        // the notice, and a failing notice (no window yet, shutting down) must not cost the log
        // line. REJECTED: one try - the second side would be skipped whenever the first threw,
        // which is precisely the situation worth recording.
        try { log(ex); } catch { }
        try { notify(ex); } catch { }
        return true;
    }
}

namespace LocalScribe.App.Services;

/// <summary>Startup background sequence (design 7.1/4.3): recovery scan first, index rebuild
/// strictly AFTER it. Runs as a background task kicked off post-tray-up (Task 24); NEVER blocks
/// Start or the UI - it merely reads/writes through MaintenanceService's per-session queue.
/// Delegate-injected (not MaintenanceService itself) so tests gate it on a
/// TaskCompletionSource. Recovered count -> one tray balloon via notify; per-session failures
/// -> IUiErrorReporter.Report each, never swallowed, never fatal to the rebuild. ScanCompleted
/// always completes (even on fault/cancel) - the Sessions page "checking for interrupted
/// sessions..." banner must always clear.</summary>
public sealed class StartupOrchestrator
{
    private readonly Func<CancellationToken, Task<RecoveryScanResult>> _recoverAll;
    private readonly Func<CancellationToken, Task> _rebuildIndex;
    private readonly IUiErrorReporter _errors;
    private readonly Action<string> _notify;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupOrchestrator(Func<CancellationToken, Task<RecoveryScanResult>> recoverAll,
        Func<CancellationToken, Task> rebuildIndex, IUiErrorReporter errors, Action<string> notify)
        => (_recoverAll, _rebuildIndex, _errors, _notify) = (recoverAll, rebuildIndex, errors, notify);

    public Task ScanCompleted => _done.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var result = await _recoverAll(ct);
            if (result.RecoveredIds.Count > 0)
                _notify($"Recovered {result.RecoveredIds.Count} interrupted session(s)");
            foreach ((string id, string error) in result.Failures)
                _errors.Report("Recovery of session " + id, new InvalidOperationException(error));
            await _rebuildIndex(ct);        // design 4.3: launch rebuild runs AFTER the scan
        }
        catch (OperationCanceledException) { }   // app shutting down mid-scan - nothing to report
        catch (Exception ex) { _errors.Report("Startup scan", ex); }
        finally { _done.TrySetResult(); }
    }
}

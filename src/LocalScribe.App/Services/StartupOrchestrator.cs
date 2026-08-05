using LocalScribe.Core.Diagnostics;
namespace LocalScribe.App.Services;

/// <summary>Startup background sequence (design 7.1/4.3): recovery scan first, index rebuild
/// strictly AFTER it. Runs as a background task kicked off post-tray-up (Task 24); NEVER blocks
/// Start or the UI - it merely reads/writes through MaintenanceService's per-session queue.
/// Delegate-injected (not MaintenanceService itself) so tests gate it on a
/// TaskCompletionSource. Recovered count -> one tray balloon via IUiErrorReporter.Info
/// (TrayNoticeReporter forwards it to the balloon and to the diagnostic log); per-session failures
/// -> IUiErrorReporter.Report each, never swallowed, never fatal to the rebuild. ScanCompleted
/// always completes (even on fault/cancel) - the Sessions page "checking for interrupted
/// sessions..." banner must always clear.</summary>
public sealed class StartupOrchestrator
{
    private readonly Func<CancellationToken, Task<RecoveryScanResult>> _recoverAll;
    private readonly Func<CancellationToken, Task> _rebuildIndex;
    private readonly IUiErrorReporter _errors;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupOrchestrator(Func<CancellationToken, Task<RecoveryScanResult>> recoverAll,
        Func<CancellationToken, Task> rebuildIndex, IUiErrorReporter errors)
        => (_recoverAll, _rebuildIndex, _errors) = (recoverAll, rebuildIndex, errors);

    public Task ScanCompleted => _done.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var result = await _recoverAll(ct);
            if (result.RecoveredIds.Count > 0)
                // Tier 1 plan A (2026-08-05): through the REPORTER, not the raw notify sink.
                // TrayNoticeReporter.Info still calls notify(message), so the balloon text is
                // unchanged - but the summary now also reaches the diagnostic log, on the same
                // path as the per-session failures below and with no duplicate. REJECTED: logging
                // inside App.xaml.cs's notify lambda - Report() calls notify() too, so every
                // failure would have been written twice, once as error and once as info.
                // privileged: false (fix round 2, 2026-08-05, Important finding): this message is
                // a bare count plus fixed text, nothing identifying - marking it by default would
                // destroy the count on disk at IncludeTranscriptText = false and mislead a reader
                // into thinking something was hidden. See IUiErrorReporter.Info's doc for the rule.
                _errors.Info($"Recovered {result.RecoveredIds.Count} interrupted session(s)",
                    privileged: false);
            // id embeds the session TITLE (SessionId.cs mints yyyy-MM-dd_HHmm_{App}_{Slug(title)}),
            // i.e. the matter/client name - mark ONLY this variable part (fix round 1, 2026-08-05,
            // Critical finding); the reporter strips the marker again for the tray balloon and
            // only the log copy stays governed by Settings.Logging.IncludeTranscriptText.
            foreach ((string id, string error) in result.Failures)
                _errors.Report("Recovery of session " + DiagnosticRedaction.Mark(id),
                    new InvalidOperationException(error));
            await _rebuildIndex(ct);        // design 4.3: launch rebuild runs AFTER the scan
        }
        catch (OperationCanceledException) { }   // app shutting down mid-scan - nothing to report
        catch (Exception ex) { _errors.Report("Startup scan", ex); }
        finally { _done.TrySetResult(); }
    }
}

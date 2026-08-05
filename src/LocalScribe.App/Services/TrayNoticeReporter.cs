using LocalScribe.Core.Diagnostics;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter for startup/background work (design 7.5: background operations
/// surface via tray balloon, not an InfoBar). WPF-free: App injects a dispatcher-marshaled
/// TrayIconHost.ShowNotice hook as the notify sink.
///
/// Tier 1 plan A (2026-08-05): same optional log sink as InfoBarErrorReporter, and it matters more
/// here - Focus Assist suppresses tray balloons outright, so for a recovery failure the log line
/// can be the ONLY record that survives. Same marking rule too: fixed-literal Report contexts go
/// bare, caller-composed Info messages go MARKED.</summary>
public sealed class TrayNoticeReporter(Action<string> notify, IDiagnosticLog? log = null)
    : IUiErrorReporter
{
    public void Report(string context, Exception ex)
    {
        log?.Write(DiagnosticLevels.Error, "startup", context, DiagnosticRedaction.ForException(ex));
        notify(context + ": " + ex.Message);
    }

    public void Info(string message)
    {
        // MARKED for the same reason as InfoBarErrorReporter.Info - an IUiErrorReporter.Info string
        // is composed by its caller and can carry a name, a title or a file path. The balloon text
        // below is unchanged; only the log copy is delimited.
        log?.Write(DiagnosticLevels.Info, "startup", DiagnosticRedaction.Mark(message));
        notify(message);
    }
}

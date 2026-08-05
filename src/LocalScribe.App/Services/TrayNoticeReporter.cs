using LocalScribe.Core.Diagnostics;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter for startup/background work (design 7.5: background operations
/// surface via tray balloon, not an InfoBar). WPF-free: App injects a dispatcher-marshaled
/// TrayIconHost.ShowNotice hook as the notify sink.
///
/// Tier 1 plan A (2026-08-05): same optional log sink as InfoBarErrorReporter, and it matters more
/// here - Focus Assist suppresses tray balloons outright, so for a recovery failure the log line
/// can be the ONLY record that survives.
///
/// Same marking rule as InfoBarErrorReporter (fix round 1, 2026-08-05, Critical finding: an
/// earlier version of this comment claimed Report contexts are always fixed literals, and
/// StartupOrchestrator.cs's own "Recovery of session " + id call site - a session id embeds the
/// session TITLE, SessionId.cs mints yyyy-MM-dd_HHmm_{App}_{Slug(title)} - proved that false).
/// Report contexts stay literal except where a call site needs a variable part, in which case
/// ONLY that part is wrapped in DiagnosticRedaction.Mark; Report strips the marker again with
/// DiagnosticRedaction.Apply(context, includeTranscriptText: true) before it reaches the balloon,
/// so notify() never sees a literal "&lt;&lt;"/">>" and the balloon text is unchanged either way.
/// The still-marked context reaches Write(), so Settings.Logging.IncludeTranscriptText decides
/// whether the LOG gets the real id. Info messages are caller-composed and go to the log MARKED
/// unconditionally, same as InfoBarErrorReporter.Info.</summary>
public sealed class TrayNoticeReporter(Action<string> notify, IDiagnosticLog? log = null)
    : IUiErrorReporter
{
    public void Report(string context, Exception ex)
    {
        log?.Write(DiagnosticLevels.Error, "startup", context, DiagnosticRedaction.ForException(ex));
        // See the class comment: Apply(..., true) strips any Mark() the call site added, always,
        // for display - the balloon must show the id either way.
        string shown = DiagnosticRedaction.Apply(context, includeTranscriptText: true) ?? context;
        notify(shown + ": " + ex.Message);
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

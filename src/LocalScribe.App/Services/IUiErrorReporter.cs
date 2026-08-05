namespace LocalScribe.App.Services;

/// <summary>Per-command error surfacing seam (design 7.5): manager/editor commands catch and
/// Report(context, ex); background operations (scan, rebuild, cascades) Info(...) their
/// outcomes. Nothing relies on the dispatcher handler for correctness; both
/// implementations write every Report/Info to the diagnostic log (Tier 1 plan A, 2026-08-05), and
/// the dispatcher exception is now recorded too - UnhandledExceptionRecorder. Both implementations
/// log the Info MESSAGE marked as privileged (DiagnosticRedaction.Mark) unconditionally, because an
/// Info string is composed by its caller and routinely carries a participant name, a session title
/// or an export path. Keep Report contexts literal - two call sites need a variable part (a
/// session id) instead, and mark ONLY that part at the call site (fix round 1, 2026-08-05: a
/// version of this rule that called every context "a fixed literal" was contradicted by exactly
/// those two call sites, and shipped the Critical it warns against here). Both reporters strip the
/// marker again before the text reaches the user, so Report's user-visible text is unaffected
/// either way - only the log copy is governed by Settings.Logging.IncludeTranscriptText.</summary>
public interface IUiErrorReporter
{
    void Report(string context, Exception ex);
    void Info(string message);
}

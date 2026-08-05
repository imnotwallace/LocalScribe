namespace LocalScribe.App.Services;

/// <summary>Per-command error surfacing seam (design 7.5): manager/editor commands catch and
/// Report(context, ex); background operations (scan, rebuild, cascades) Info(...) their
/// outcomes. Nothing relies on the dispatcher handler for correctness; both
/// implementations write every Report/Info to the diagnostic log (Tier 1 plan A, 2026-08-05), and
/// the dispatcher exception is now recorded too - UnhandledExceptionRecorder. Both implementations
/// log the Info MESSAGE marked as privileged (DiagnosticRedaction.Mark) and the Report CONTEXT
/// bare: a context is a fixed literal at every call site, an Info string is composed by its caller
/// and routinely carries a participant name, a session title or an export path. Keep contexts
/// literal - if you ever need a name in one, mark it at that call site.</summary>
public interface IUiErrorReporter
{
    void Report(string context, Exception ex);
    void Info(string message);
}

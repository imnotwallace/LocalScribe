namespace LocalScribe.App.Services;

/// <summary>Per-command error surfacing seam (design 7.5): manager/editor commands catch and
/// Report(context, ex); background operations (scan, rebuild, cascades) Info(...) their
/// outcomes. Nothing relies on the globally-swallowed DispatcherUnhandledException. Stage 7
/// attaches real logging behind this seam.</summary>
public interface IUiErrorReporter
{
    void Report(string context, Exception ex);
    void Info(string message);
}

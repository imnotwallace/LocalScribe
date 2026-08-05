using System.Collections.ObjectModel;
using LocalScribe.Core.Diagnostics;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter surfacing into MainWindow's InfoBar (design 7.5). WPF-free: the
/// queue is plain ObservableCollection state; Report/Info may be called from any thread and
/// marshal through the injected dispatch (the UI thread in the app, an inline runner in
/// tests). MainWindow mirrors Messages[0] into the InfoBar and calls DismissOldest when the
/// user closes it; the collection outlives any single MainWindow instance, so errors queued
/// while the window is closed appear on next open.
///
/// Tier 1 plan A (2026-08-05): the optional log sink is a PARAMETER, not a decorator - this class
/// is consumed concretely (MainWindowViewModel.cs:14 declares InfoBarErrorReporter Errors, and
/// MainWindow.xaml.cs reads .Messages/.DismissOldest()), so a decorator at the App.xaml.cs
/// construction site would not compile. Defaulted null so every existing test keeps building.
///
/// The Report CONTEXT reaches the log bare - every verified call site passes a fixed literal
/// ("Export", "Delete session", "Tag session " + sessionId). The Info MESSAGE reaches it MARKED,
/// because callers compose party-identifying text into it: MetadataEditorViewModel.cs:369 puts a
/// roster member's real NAME in it and ExportDialogViewModel.cs:197 puts a destination path built
/// from the session title (the matter/client name) in it. Unmarked, both would land in
/// diagnostics\ at the DEFAULT settings - an undeclared copy of privileged identifiers outside
/// every retention and purge path.</summary>
public sealed class InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)
    : IUiErrorReporter
{
    public ObservableCollection<string> Messages { get; } = [];

    public void Report(string context, Exception ex)
    {
        // Log BEFORE dispatching: the queue is drained by a window that may never open, and the
        // durable record must not depend on the user seeing the InfoBar.
        log?.Write(DiagnosticLevels.Error, "ui", context, DiagnosticRedaction.ForException(ex));
        dispatch(() => Messages.Add(context + ": " + ex.Message));
    }

    public void Info(string message)
    {
        // MARKED - see the class comment. The InfoBar itself still shows the raw message below;
        // only the log copy is delimited, so Settings.Logging.IncludeTranscriptText governs it.
        // REJECTED: marking at the CALL SITES instead - there are twenty-odd of them across six
        // view models, a new one lands most rounds, and forgetting the wrapper is silent.
        log?.Write(DiagnosticLevels.Info, "ui", DiagnosticRedaction.Mark(message));
        dispatch(() => Messages.Add(message));
    }

    public void DismissOldest()
    {
        if (Messages.Count > 0) Messages.RemoveAt(0);
    }
}

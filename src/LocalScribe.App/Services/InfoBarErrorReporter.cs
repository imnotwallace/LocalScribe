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
/// The Report CONTEXT is meant to stay a literal at the call site ("Export", "Delete session").
/// Two verified call sites need a variable part - StartupOrchestrator.cs "Recovery of session " +
/// id and MattersPageViewModel.cs "Tag session " + sessionId - and a session id embeds the
/// session TITLE (SessionId.cs mints yyyy-MM-dd_HHmm_{App}_{Slug(title)}), i.e. the matter/client
/// name (fix round 1, 2026-08-05, Critical finding: the earlier version of this comment offered
/// that exact concatenation as PROOF no concatenation ever happens - self-contradicting, and the
/// reason the leak shipped). Those call sites wrap ONLY the variable part in
/// DiagnosticRedaction.Mark; Report here strips the marker again with
/// DiagnosticRedaction.Apply(context, includeTranscriptText: true) before the text ever reaches
/// Messages, so the InfoBar shows the exact string it always has and never shows a literal
/// "&lt;&lt;"/">>". The still-marked context is what reaches Write(), so
/// Settings.Logging.IncludeTranscriptText - not this class - decides whether the LOG gets the
/// real id or [redacted].
///
/// The Info MESSAGE reaches the log MARKED by DEFAULT, because callers compose
/// party-identifying text into it: MetadataEditorViewModel.cs:369 puts a roster member's real
/// NAME in it and ExportDialogViewModel.cs:197 puts a destination path built from the session
/// title (the matter/client name) in it. Unmarked, both would land in diagnostics\ at the DEFAULT
/// settings - an undeclared copy of privileged identifiers outside every retention and purge
/// path. See IUiErrorReporter's doc for the narrow privileged: false opt-out (fix round 2,
/// 2026-08-05) and why it exists.</summary>
public sealed class InfoBarErrorReporter(Action<Action> dispatch, IDiagnosticLog? log = null)
    : IUiErrorReporter
{
    public ObservableCollection<string> Messages { get; } = [];

    /// <summary>Severity of Messages[i], at the SAME index and always the SAME length (Tier 1
    /// plan D, T1-5, 2026-08-05). A parallel collection rather than making Messages hold a
    /// record: MainWindow.xaml.cs and MainWindowViewModel.cs consume this class CONCRETELY and
    /// InfoBarErrorReporterTests asserts Messages against a string[], so changing the element
    /// type breaks pinned tests for no user-visible gain. Lockstep is maintained in exactly two
    /// places - Add and DismissOldest.</summary>
    public ObservableCollection<NoticeSeverity> Severities { get; } = [];

    // Severities FIRST, Messages second: MainWindow.SyncInfoBar runs off
    // Messages.CollectionChanged and reads Severities[0] in that same turn, so the severity for
    // the new head must already be in place when the message lands.
    //
    // Add does NO logging (Tier 1 plan D, 2026-08-05). REJECTED: moving Plan A's log?.Write calls
    // in here to share them - Report and Info write DIFFERENT payloads on purpose (a four-argument
    // structured line with DiagnosticRedaction.ForException(ex) as the detail, versus a
    // three-argument info line with the marked message), and Add only ever sees the already
    // concatenated "context: message" string, which makes ForException's per-exception marking and
    // per-exception stack neutralisation structurally unreachable and puts the raw ex.Message in
    // diagnostics unmarked. Add is also called from INSIDE dispatch; the durable record must not
    // depend on the dispatcher ever running.
    private void Add(string message, NoticeSeverity severity)
    {
        Severities.Add(severity);
        Messages.Add(message);
    }

    public void Report(string context, Exception ex)
    {
        // Log BEFORE dispatching: the queue is drained by a window that may never open, and the
        // durable record must not depend on the user seeing the InfoBar.
        log?.Write(DiagnosticLevels.Error, "ui", context, DiagnosticRedaction.ForException(ex));
        // Fix round 1 (2026-08-05, Critical finding): context may carry a Mark()-wrapped id from
        // the call site (see the class comment). Apply(..., includeTranscriptText: true) always
        // strips the markers for DISPLAY, independent of Settings.Logging.IncludeTranscriptText -
        // the InfoBar must show the id either way, it is only the LOG copy that switch governs.
        // A context with no marker (every other call site) passes through Apply() unchanged.
        string shown = DiagnosticRedaction.Apply(context, includeTranscriptText: true) ?? context;
        dispatch(() => Add(shown + ": " + ex.Message, NoticeSeverity.Error));
    }

    public void Info(string message, bool privileged = true)
    {
        // MARKED by default - see the interface doc. The InfoBar itself still shows the raw
        // message below; only the log copy is delimited, so Settings.Logging.IncludeTranscriptText
        // governs it. privileged: false is an explicit, call-site-justified assertion that message
        // carries nothing identifying (fix round 2, 2026-08-05) - REJECTED: marking at the CALL
        // SITES instead of defaulting - there are twenty-odd of them across six view models, a new
        // one lands most rounds, and forgetting the wrapper is silent.
        log?.Write(DiagnosticLevels.Info, "ui", privileged ? DiagnosticRedaction.Mark(message) : message);
        dispatch(() => Add(message, NoticeSeverity.Informational));
    }

    public void Info(string message, NoticeSeverity severity)
    {
        // The severity overload is the one NEW body. It logs the same way Info(string, bool) does -
        // marked by default - and maps the severity onto the level vocabulary the shared contract
        // defines. REJECTED: a two-arm `severity == NoticeSeverity.Error ? "error" : "info"` - it
        // writes a Warning notice at "info", so a user who sets Settings.Logging.Level to "warn" to
        // cut noise SILENTLY loses every warning the app raised. Losing warnings is the opposite of
        // what that setting is for.
        string level = severity switch
        {
            NoticeSeverity.Error => DiagnosticLevels.Error,
            NoticeSeverity.Warning => DiagnosticLevels.Warn,
            _ => DiagnosticLevels.Info,
        };
        log?.Write(level, "ui", DiagnosticRedaction.Mark(message));
        dispatch(() => Add(message, severity));
    }

    public void DismissOldest()
    {
        if (Messages.Count == 0) return;
        Severities.RemoveAt(0);
        Messages.RemoveAt(0);
    }
}

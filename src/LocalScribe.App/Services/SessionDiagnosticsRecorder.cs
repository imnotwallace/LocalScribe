using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
namespace LocalScribe.App.Services;

/// <summary>Turns SessionController's EXISTING event surface into diagnostic lines (Tier 1 plan A,
/// 2026-08-05, spec item T1-1: "session start/stop/recovery, transcription downgrades"). No Core
/// change was needed for the downgrades: TranscriptionWorker raises "VRAM_OOM"
/// (TranscriptionWorker.cs:108) and "RTF_LAGGING" (:128), and SessionController re-raises them
/// verbatim (:516) - they were simply never recorded anywhere.
///
/// WPF-free, and it exposes plain METHODS rather than subscribing itself: App.OnStartup does the
/// four "+=" lines and tests call the methods directly, which is the only way any of this gets
/// coverage (App.xaml.cs has none). The session id is read through a delegate, never captured -
/// CurrentSessionId is null again by the time Idle arrives.
///
/// NEVER logs transcript text: the controller's Notice strings are fixed operator messages and no
/// segment text passes through here, so nothing on this path needs a redaction marker on the
/// message itself. The id in every Detail field IS privileged though - SessionId.cs mints
/// yyyy-MM-dd_HHmm_{App}_{Slug(title)}, i.e. the matter/client name - so Where() and
/// FinalizeCompleted mark it (fix round 1 shape: mark ONLY the variable part, never the fixed
/// "session=" prefix), same as StartupOrchestrator's per-session failure context. This is a
/// DIFFERENT id than the one that can arrive via Notice's message argument (see the class's
/// task-8-report.md note on CompositionRoot.cs's ExternalEngineBusy) - that one is a caller-
/// composed string this class cannot safely introspect without a cross-file display-side fix, and
/// is deliberately left for a follow-up rather than guessed at here.</summary>
public sealed class SessionDiagnosticsRecorder(IDiagnosticLog log, Func<string?> sessionId)
{
    public void StateChanged(SessionState state)
        => log.Write(DiagnosticLevels.Info, "session", "State " + state, Where());

    public void ErrorRaised(string code)
        => log.Write(DiagnosticLevels.Warn, "session", code switch
        {
            "VRAM_OOM" => "Transcription downgraded - VRAM exhausted",
            "RTF_LAGGING" => "Transcription downgraded - sustained lag behind realtime",
            "TRANSCRIPTION_FAILED" => "Live transcription stopped - audio still recording",
            "SILENT_SOURCE" => "A capture leg went silent",
            // Unknown codes are recorded verbatim, never dropped: Plans B and C add more of them,
            // and an unrecognised code is exactly the one worth seeing in a support file.
            _ => "Session error " + code,
        }, "code=" + code + " " + Where());

    public void Notice(string message) => log.Write(DiagnosticLevels.Info, "session", message, Where());

    /// <summary>Fires from the background finalize drain, AFTER the controller is Idle again - so
    /// the id arrives as an argument rather than from the live probe, which is null by then.</summary>
    public void FinalizeCompleted(string finalizedSessionId)
        => log.Write(DiagnosticLevels.Info, "session", "Finalize completed",
            "session=" + DiagnosticRedaction.Mark(finalizedSessionId));

    // "(none)" is a fixed literal, not privileged - mark ONLY a real id, otherwise Apply() would
    // render the no-session case as "[redacted]" at the default setting, which misleadingly
    // implies something was hidden when nothing was.
    private string Where() => "session=" + (sessionId() is string id ? DiagnosticRedaction.Mark(id) : "(none)");
}

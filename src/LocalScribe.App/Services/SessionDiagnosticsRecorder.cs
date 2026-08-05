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
/// NEVER logs transcript text: no segment text passes through here. The id in every Detail field IS
/// privileged though - SessionId.cs mints yyyy-MM-dd_HHmm_{App}_{Slug(title)}, i.e. the
/// matter/client name - so Where() and FinalizeCompleted mark it (fix round 1 shape: mark ONLY the
/// variable part, never the fixed "session=" prefix), same as StartupOrchestrator's per-session
/// failure context.
///
/// The Notice MESSAGE is a different case, and the reason is not "a Notice is always a literal" -
/// it is not. SessionController re-raises the caller-composed ExternalEngineBusy string, and
/// CompositionRoot.cs interpolates a re-transcription SESSION ID into it. That id is marked AT ITS
/// SOURCE with DiagnosticRedaction.Mark(rid) and stripped again at the single display boundary in
/// SessionViewModel.cs - the mark-at-source / strip-at-display pattern, SHARED-CONTRACT section 1a.
/// So this class may log a Notice message WHOLE only because every composing call site marks its
/// own variable part; a NEW Notice that interpolates an id (Plans B and C add more of them) must do
/// the same at its own call site, because nothing here can introspect a string it did not compose.
/// This is CLOSED, not open: it shipped in 6bc5345.</summary>
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

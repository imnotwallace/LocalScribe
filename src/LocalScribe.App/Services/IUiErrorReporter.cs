namespace LocalScribe.App.Services;

/// <summary>Per-command error surfacing seam (design 7.5): manager/editor commands catch and
/// Report(context, ex); background operations (scan, rebuild, cascades) Info(...) their
/// outcomes. Nothing relies on the dispatcher handler for correctness; both
/// implementations write every Report/Info to the diagnostic log (Tier 1 plan A, 2026-08-05), and
/// the dispatcher exception is now recorded too - UnhandledExceptionRecorder. Both implementations
/// log the Info MESSAGE marked as privileged (DiagnosticRedaction.Mark) by DEFAULT, because an
/// Info string is composed by its caller and routinely carries a participant name, a session title
/// or an export path. Keep Report contexts literal - two call sites need a variable part (a
/// session id) instead, and mark ONLY that part at the call site (fix round 1, 2026-08-05: a
/// version of this rule that called every context "a fixed literal" was contradicted by exactly
/// those two call sites, and shipped the Critical it warns against here). Both reporters strip the
/// marker again before the text reaches the user, so Report's user-visible text is unaffected
/// either way - only the log copy is governed by Settings.Logging.IncludeTranscriptText.
///
/// <c>privileged</c> (fix round 2, 2026-08-05, Important finding): a narrow, explicit opt-out from
/// the marked-by-default rule above. Marking is safe but not free - a bare count with fixed text
/// (StartupOrchestrator's "Recovered N interrupted session(s)") carries nothing identifying, and
/// wholesale marking destroys it on disk at the default Settings.Logging.IncludeTranscriptText =
/// false, misleading a reader into thinking something was hidden when nothing was - the same
/// principle SessionDiagnosticsRecorder.Where() already applies to "(none)". <c>privileged: false</c>
/// is an explicit assertion, made and justified AT THE CALL SITE, that the message is composed
/// solely of fixed text and non-identifying values (a count, an enum name, a program-defined
/// token) - never a name, a title, a path or free text a caller only partially controls. REJECTED:
/// marking at every call site instead of defaulting - there are twenty-odd Info call sites across
/// six view models, a new one lands most rounds, and forgetting the wrapper is silent; that is
/// exactly how two Criticals already reached disk in this plan. Defaulting to marked and requiring
/// an explicit, justified opt-out per call site keeps that failure mode closed while still letting
/// a genuinely safe line reach disk intact.</summary>
public interface IUiErrorReporter
{
    void Report(string context, Exception ex);
    void Info(string message, bool privileged = true);

    /// <summary>Info with an explicit bar colour (Tier 1 plan D, T1-5, 2026-08-05). A DEFAULT
    /// INTERFACE METHOD on purpose: 26 types implement this interface (2 production, 24 test
    /// fakes) and only InfoBarErrorReporter can do anything with a severity - a tray balloon has
    /// no such concept. A default that DISCARDS the severity lets every other implementer stay
    /// untouched and keeps its existing assertions green.
    /// REJECTED: an abstract second overload (26 edits, 24 of them meaningless) and changing
    /// Info's existing signature (breaks all 32 production call sites in one commit).</summary>
    void Info(string message, NoticeSeverity severity) => Info(message);
}

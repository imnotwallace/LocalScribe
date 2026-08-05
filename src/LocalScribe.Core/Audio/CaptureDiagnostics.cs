namespace LocalScribe.Core.Audio;

/// <summary>Attaches a diagnostic sink to a capture source that has one (Tier 1 plan A,
/// 2026-08-05). Returns the SAME instance so a call site reads as a wrap:
/// <c>var s = CaptureDiagnostics.Attach(new ProcessLoopbackCapture(pid, clock), _diagnostic);</c>
/// Nothing is ever unsubscribed: the sink outlives every source (it is the process-wide log) and
/// the source is disposed with the leg.</summary>
public static class CaptureDiagnostics
{
    public static ICaptureSource Attach(ICaptureSource source, Action<string>? sink)
    {
        if (sink is not null && source is IDiagnosticSource diagnostic) diagnostic.Diagnostic += sink;
        return source;
    }
}

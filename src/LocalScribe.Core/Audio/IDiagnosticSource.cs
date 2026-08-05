namespace LocalScribe.Core.Audio;

/// <summary>A capture source that can explain what it did (Tier 1 plan A, 2026-08-05).
/// ProcessLoopbackCapture has raised these lines since the Stage-1 spike - activation format
/// fallbacks, device-invalidated recovery - but the ONLY subscriber in the solution was
/// SpikeRunner/Program.cs:55, a console harness, so none of it was visible in the shipping app.
/// Deliberately separate from ICaptureSource: MicCaptureSource has nothing to say, and widening
/// the capture contract for one implementation would force an empty event onto every source and
/// every test double.</summary>
public interface IDiagnosticSource
{
    event Action<string>? Diagnostic;
}

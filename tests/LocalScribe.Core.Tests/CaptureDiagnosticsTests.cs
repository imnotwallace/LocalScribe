using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Tests;

/// <summary>Attaching a diagnostic sink to a capture source that has one (Tier 1 plan A,
/// 2026-08-05). ProcessLoopbackCapture has raised these lines since the Stage-1 spike and only
/// SpikeRunner/Program.cs:55 ever subscribed - the shipping app never saw an activation fallback
/// or a device-invalidated recovery. ProcessLoopbackCapture itself cannot be unit-tested (it
/// activates real WASAPI), so the SEAM is tested here over a fake that raises on demand.</summary>
public sealed class CaptureDiagnosticsTests
{
    /// <summary>A capture source that can talk. RaiseDiagnostic drives the event synchronously -
    /// the house fake shape (explicit RaiseXxx, never an assertion inside the fake).</summary>
    private sealed class TalkingSource : ICaptureSource, IDiagnosticSource
    {
        public SourceKind Source => SourceKind.Remote;
        public event Action<AudioFrame>? FrameAvailable;
        public event Action<string>? Diagnostic;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseDiagnostic(string message) => Diagnostic?.Invoke(message);
        public void RaiseFrame(AudioFrame frame) => FrameAvailable?.Invoke(frame);
    }

    private sealed class SilentSource : ICaptureSource
    {
        public SourceKind Source => SourceKind.Local;
        public event Action<AudioFrame>? FrameAvailable;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void RaiseFrame(AudioFrame frame) => FrameAvailable?.Invoke(frame);
    }

    [Fact]
    public void Attach_forwards_every_diagnostic_line_and_returns_the_same_instance()
    {
        var lines = new List<string>();
        var source = new TalkingSource();

        var returned = CaptureDiagnostics.Attach(source, lines.Add);

        Assert.Same(source, returned);
        source.RaiseDiagnostic("activation fell back to native format 48000/2");
        source.RaiseDiagnostic("re-established after AUDCLNT_E_DEVICE_INVALIDATED");
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("activation fell back", lines[0]);
    }

    [Fact]
    public void Attach_no_ops_for_a_source_with_nothing_to_say()
    {
        // MicCaptureSource has no Diagnostic event, which is exactly why this is a SEPARATE
        // interface rather than a member of ICaptureSource.
        var source = new SilentSource();
        Assert.Same(source, CaptureDiagnostics.Attach(source, _ => { }));
    }

    [Fact]
    public void Attach_no_ops_for_a_null_sink()
    {
        var source = new TalkingSource();
        CaptureDiagnostics.Attach(source, null);
        source.RaiseDiagnostic("nobody listening");   // must not throw
    }
}

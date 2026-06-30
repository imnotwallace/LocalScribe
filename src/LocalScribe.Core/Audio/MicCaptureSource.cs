// src/LocalScribe.Core/Audio/MicCaptureSource.cs
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace LocalScribe.Core.Audio;

/// <summary>Captures the default communications mic, downmixes + resamples to
/// 16 kHz mono, emits AudioFrames stamped on the session clock.</summary>
public sealed class MicCaptureSource : ICaptureSource
{
    private readonly IClock _clock;
    private readonly WasapiCapture _capture;
    private readonly MonoResampler16k _resampler;
    private readonly int _channels;

    public SourceKind Source => SourceKind.Local;
    public event Action<AudioFrame>? FrameAvailable;

    public MicCaptureSource(IClock clock)
    {
        _clock = clock;
        var device = new MMDeviceEnumerator()
            .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _capture = new WasapiCapture(device);             // device mix format
        _channels = _capture.WaveFormat.Channels;
        _resampler = new MonoResampler16k(_capture.WaveFormat.SampleRate);
        _capture.DataAvailable += OnData;
    }

    private void OnData(object? _, WaveInEventArgs e)
    {
        // WASAPI mix format is typically 32-bit float interleaved.
        int floatCount = e.BytesRecorded / 4;
        var interleaved = new float[floatCount];
        Buffer.BlockCopy(e.Buffer, 0, interleaved, 0, e.BytesRecorded);

        float[] mono = _channels == 1
            ? interleaved
            : PcmConverter.StereoToMono(interleaved);     // assumes 2ch; see note

        float[] mono16k = _resampler.Process(mono);
        if (mono16k.Length > 0)
            FrameAvailable?.Invoke(new AudioFrame(Source, _clock.ElapsedMs, mono16k));
    }

    public void Start() => _capture.StartRecording();
    public void Stop()  => _capture.StopRecording();
    public void Dispose() { _capture.DataAvailable -= OnData; _capture.Dispose(); }
}

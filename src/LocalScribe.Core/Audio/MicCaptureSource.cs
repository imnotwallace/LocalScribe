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
    private readonly bool _isFloat;   // true = 32-bit IEEE float mix format; false = 16-bit PCM

    public SourceKind Source => SourceKind.Local;
    public event Action<AudioFrame>? FrameAvailable;

    /// <summary>Friendly name of the capture device local.wav is recording from.</summary>
    public string DeviceName { get; }

    /// <param name="role">Which default capture endpoint to use. Communications = the "Default
    /// Communication Device" (matches how meeting apps route the mic); Multimedia/Console = the
    /// plain "Default Device". Switch if the mic is not coming through on the comms default.</param>
    public MicCaptureSource(IClock clock, Role role = Role.Communications)
    {
        _clock = clock;
        var device = new MMDeviceEnumerator()
            .GetDefaultAudioEndpoint(DataFlow.Capture, role);
        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device);             // device mix format
        var fmt = _capture.WaveFormat;
        _channels = fmt.Channels;
        // Shared-mode mix format is effectively always 32-bit float, but validate so a non-float
        // endpoint fails loudly instead of writing garbage to local.wav (Task 7 note).
        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        bool isPcm16 = fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16;
        if (!_isFloat && !isPcm16)
            throw new NotSupportedException(
                $"Unsupported mic mix format: {fmt.Encoding} {fmt.BitsPerSample}-bit, {fmt.Channels}ch. " +
                "Expected 32-bit IEEE float or 16-bit PCM.");
        _resampler = new MonoResampler16k(fmt.SampleRate);
        _capture.DataAvailable += OnData;
    }

    private void OnData(object? _, WaveInEventArgs e)
    {
        float[] interleaved;
        if (_isFloat)
        {
            int floatCount = e.BytesRecorded / 4;
            interleaved = new float[floatCount];
            Buffer.BlockCopy(e.Buffer, 0, interleaved, 0, e.BytesRecorded);
        }
        else   // 16-bit PCM (validated in the constructor)
        {
            interleaved = PcmConverter.Int16BytesToFloat(e.Buffer.AsSpan(0, e.BytesRecorded));
        }

        float[] mono = _channels switch
        {
            1 => interleaved,
            2 => PcmConverter.StereoToMono(interleaved),
            _ => DownmixToMono(interleaved, _channels),   // some headsets enumerate >2 channels
        };

        float[] mono16k = _resampler.Process(mono);
        if (mono16k.Length > 0)
            FrameAvailable?.Invoke(new AudioFrame(Source, _clock.ElapsedMs, mono16k));
    }

    /// <summary>Averages an interleaved N-channel buffer down to mono.</summary>
    private static float[] DownmixToMono(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        var outp = new float[frames];
        float inv = 1f / channels;
        for (int i = 0; i < frames; i++)
        {
            float sum = 0f;
            int baseIdx = i * channels;
            for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
            outp[i] = sum * inv;
        }
        return outp;
    }

    public void Start() => _capture.StartRecording();
    public void Stop()  => _capture.StopRecording();
    public void Dispose() { _capture.DataAvailable -= OnData; _capture.Dispose(); }
}

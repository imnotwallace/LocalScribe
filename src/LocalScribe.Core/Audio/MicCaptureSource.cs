// src/LocalScribe.Core/Audio/MicCaptureSource.cs
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace LocalScribe.Core.Audio;

/// <summary>Captures the default communications mic, downmixes + resamples to
/// 16 kHz mono, emits AudioFrames stamped on the session clock.</summary>
public sealed class MicCaptureSource : ICaptureSource, IEndpointMuteObservable
{
    private readonly IClock _clock;
    private readonly WasapiCapture _capture;
    private readonly MonoResampler16k _resampler;
    private readonly int _channels;
    private readonly bool _isFloat;   // true = 32-bit IEEE float mix format; false = 16-bit PCM
    private readonly MMDevice _device;
    private bool _lastDeviceMuted;

    public SourceKind Source => SourceKind.Local;
    public event Action<AudioFrame>? FrameAvailable;

    /// <summary>Endpoint (device master) mute state (design 2026-07-10 section 2). Fail-open: an
    /// endpoint without a volume interface reads false rather than throwing.</summary>
    public bool DeviceMuted
    {
        get { try { return _device.AudioEndpointVolume.Mute; } catch { return false; } }
    }
    public event Action<bool>? DeviceMuteChanged;

    /// <summary>Friendly name of the capture device local.wav is recording from.</summary>
    public string DeviceName { get; }

    /// <summary>WASAPI device Id of the capture endpoint (design section 2): the id recorded into
    /// MicSnapshot when a pin was honored, empty-string-safe for the default path.</summary>
    public string DeviceId { get; }

    /// <param name="role">Which default capture endpoint to use. Communications = the "Default
    /// Communication Device" (matches how meeting apps route the mic); Multimedia/Console = the
    /// plain "Default Device".</param>
    public MicCaptureSource(IClock clock, Role role = Role.Communications)
        : this(clock, new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, role))
    {
    }

    /// <summary>Open a specific capture endpoint by its WASAPI device Id (design section 2:
    /// honoring a pinned mic). The provider only takes this path after MicCapturePlanner has
    /// confirmed the id is among the live devices; a raw NAudio failure here is a hardware race
    /// surfaced by the smoke run, not a unit-tested path.</summary>
    public MicCaptureSource(IClock clock, string deviceId)
        : this(clock, new MMDeviceEnumerator().GetDevice(deviceId))
    {
    }

    private MicCaptureSource(IClock clock, MMDevice device)
    {
        _clock = clock;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        _capture = new WasapiCapture(device);             // device mix format
        var fmt = _capture.WaveFormat;
        _channels = fmt.Channels;
        // Shared-mode mix format is effectively always 32-bit float, but validate so a non-float
        // endpoint fails loudly instead of writing garbage to local.wav.
        _isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32;
        bool isPcm16 = fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16;
        if (!_isFloat && !isPcm16)
            throw new NotSupportedException(
                $"Unsupported mic mix format: {fmt.Encoding} {fmt.BitsPerSample}-bit, {fmt.Channels}ch. " +
                "Expected 32-bit IEEE float or 16-bit PCM.");
        _resampler = new MonoResampler16k(fmt.SampleRate);
        _capture.DataAvailable += OnData;

        // Fail-open: an endpoint without a volume interface simply has no mute awareness -
        // capture must never fail because of it (design 2026-07-10 section 2).
        _device = device;
        try
        {
            _lastDeviceMuted = _device.AudioEndpointVolume.Mute;
            _device.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolume;
        }
        catch { /* no endpoint-volume interface: DeviceMuted stays false, no events */ }
    }

    private void OnEndpointVolume(AudioVolumeNotificationData data)
    {
        if (data.Muted == _lastDeviceMuted) return;             // volume-only change: not ours
        _lastDeviceMuted = data.Muted;
        DeviceMuteChanged?.Invoke(data.Muted);                  // COM callback thread; consumers marshal
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
    public void Dispose()
    {
        try { _device.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolume; } catch { }
        DeviceMuteChanged = null;
        _capture.DataAvailable -= OnData;
        _capture.Dispose();
        _device.Dispose();
    }
}

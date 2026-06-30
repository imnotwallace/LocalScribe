// src/LocalScribe.Core/Audio/ProcessLoopbackCapture.cs
//
// Per-process WASAPI loopback capture (the Stage 1 crux). Activates the
// "VAD\Process_Loopback" virtual device via ActivateAudioInterfaceAsync with
// AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK, then captures the target
// process tree's render audio and emits 16 kHz mono AudioFrames, silence-filled
// across gaps so the Remote stream stays continuous on its device timeline.
//
// Interop adapted from Microsoft's ApplicationLoopback C++ sample and NAudio
// PR #1348, against CsWin32-generated types (Task 8). See the Stage 1 loopback
// interop reference for the verified facts behind every decision here.
//
// DUAL FORMAT PATH (verify on box - the one genuine uncertainty):
//   Option A (primary): Initialize directly at 16 kHz/mono/16-bit using
//     AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM. Whether AUTOCONVERTPCM performs
//     CROSS-RATE downsampling on VAD\Process_Loopback is unconfirmed by any
//     primary source - so on Initialize failure we fall back to:
//   Option B (fallback): Initialize at a native engine format (float32) and
//     downmix+resample to 16 kHz in software via PcmConverter + MonoResampler16k.
//   Initialize is "once and only once" per IAudioClient AND throws on failure
//   (CsWin32 generates it without [PreserveSig]), so each format attempt uses a
//   freshly-activated client.
//
// Threading: ActivateCompleted is delivered on a system MTA worker thread; init
// is modeled as await over a TaskCompletionSource. Callers (SpikeRunner) run on
// an MTA thread so the synchronous Start() can block on activation safely.

using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace LocalScribe.Core.Audio;

public sealed class ProcessLoopbackCapture : ICaptureSource
{
    private const int SampleRate = WavSink.SampleRate;            // 16000
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const long BufferDurationHns = 0;                     // shared event-driven: let the engine pick

    // HRESULTs / buffer flags
    private const int AUDCLNT_E_RESOURCES_INVALIDATED = unchecked((int)0x88890026);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const uint AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY = 0x1;

    private enum FormatMode { DirectMono16k, NativeResample }

    private readonly uint _targetPid;
    private readonly bool _excludeMode;   // false = INCLUDE target tree (default); true = EXCLUDE self (Plan B)
    private readonly IClock _clock;
    private readonly EventWaitHandle _bufferReady = new(false, EventResetMode.AutoReset);

    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _pump;
    private volatile bool _running;

    private FormatMode _mode;
    private int _engineRate = SampleRate;
    private int _engineChannels = 1;
    private MonoResampler16k? _resampler;   // Option B only

    // Gap-fill state. Frame counts are in the INITIALIZED stream's frame units
    // (16 kHz for Option A; native engine rate for Option B). devicePos is reported
    // in those same units, so SilenceGapFiller math is unit-consistent.
    private long _anchorPos = -1;
    private long _writtenFrames;

    /// <summary>Diagnostics for the smoke test: which format mode/engine rate won, and the activation param size.</summary>
    public string ActivationInfo { get; private set; } = "(not started)";

    public SourceKind Source => SourceKind.Remote;
    public event Action<AudioFrame>? FrameAvailable;

    public ProcessLoopbackCapture(uint targetPid, IClock clock)
        : this(targetPid, excludeMode: false, clock) { }

    private ProcessLoopbackCapture(uint targetPid, bool excludeMode, IClock clock)
        => (_targetPid, _excludeMode, _clock) = (targetPid, excludeMode, clock);

    /// <summary>Plan B: full-system loopback minus LocalScribe's own process tree.</summary>
    public static ProcessLoopbackCapture SystemLoopbackExcludingSelf(IClock clock)
        => new((uint)Environment.ProcessId, excludeMode: true, clock);

    public void Start()
    {
        if (Environment.OSVersion.Version.Build < 20348)
            throw new PlatformNotSupportedException(
                "Per-process loopback requires Windows 10 build 20348+ (have " +
                Environment.OSVersion.Version + ").");

        ActivateAndInitialize();   // blocks until the client is live (Option A, else Option B)

        _running = true;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "ProcLoopbackPump" };
        _pump.Start();
    }

    // --- activation + initialization -------------------------------------------------

    private void ActivateAndInitialize()
    {
        int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();

        // Option A: direct 16 kHz mono 16-bit via AUTOCONVERTPCM.
        _client = TryActivateAndInitialize(channels: 1, rate: SampleRate, bits: 16, autoConvert: true);
        if (_client != null)
        {
            _mode = FormatMode.DirectMono16k;
            _engineRate = SampleRate;
            _engineChannels = 1;
        }
        else
        {
            // Option B: native engine format (float32) + software downmix/resample.
            // GetMixFormat is not valid on the loopback client (E_NOTIMPL), so probe
            // common engine formats on freshly-activated clients.
            foreach (var (rate, ch) in new (uint rate, ushort ch)[] { (48000, 2), (44100, 2), (48000, 1), (44100, 1) })
            {
                _client = TryActivateAndInitialize(ch, (int)rate, bits: 32, autoConvert: false);
                if (_client != null)
                {
                    _mode = FormatMode.NativeResample;
                    _engineRate = (int)rate;
                    _engineChannels = ch;
                    _resampler = new MonoResampler16k(_engineRate);
                    break;
                }
            }
            if (_client == null)
                throw new InvalidOperationException(
                    "Process loopback Initialize failed for Option A (16 kHz AUTOCONVERTPCM) and all " +
                    "Option B native-format candidates. Last error: " + (_lastError?.Message ?? "unknown") +
                    " (pid " + _targetPid + ", excludeMode " + _excludeMode + ").", _lastError);
        }

        _capture = GetCaptureClient(_client);
        _client.SetEventHandle(new HANDLE(_bufferReady.SafeWaitHandle.DangerousGetHandle()));
        _client.Start();

        ActivationInfo = $"mode={_mode}, engineRate={_engineRate}, engineCh={_engineChannels}, " +
                         $"paramsSize={paramsSize}, pid={_targetPid}, excludeMode={_excludeMode}";
    }

    private Exception? _lastError;

    private IAudioClient? TryActivateAndInitialize(ushort channels, int rate, ushort bits, bool autoConvert)
    {
        IAudioClient? client = null;
        try
        {
            client = ActivateClientAsync().GetAwaiter().GetResult();
            InitializeFormat(client, channels, rate, bits, autoConvert);
            return client;
        }
        catch (Exception ex) when (ex is COMException || ex.HResult < 0)
        {
            _lastError = ex;
            ReleaseCom(ref client);
            return null;
        }
    }

    private async Task<IAudioClient> ActivateClientAsync()
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS is 12 bytes: [0]=ActivationType, [4]=TargetProcessId,
        // [8]=ProcessLoopbackMode (a single-member union). Write the bytes explicitly to avoid any
        // dependence on marshalling the generated anonymous union; size is taken from the real struct.
        int paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        var paramBytes = new byte[paramsSize];   // zero-initialized
        BitConverter.TryWriteBytes(paramBytes.AsSpan(0),
            (int)AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK);
        BitConverter.TryWriteBytes(paramBytes.AsSpan(4), _targetPid);
        BitConverter.TryWriteBytes(paramBytes.AsSpan(8),
            (int)(_excludeMode
                ? PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
                : PROCESS_LOOPBACK_MODE.PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE));

        IntPtr pParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr pPropVar = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlobHeader>());
        var handler = new ActivateHandler();
        IActivateAudioInterfaceAsyncOperation? op = null;
        try
        {
            Marshal.Copy(paramBytes, 0, pParams, paramsSize);
            Marshal.StructureToPtr(new PropVariantBlobHeader
            {
                Vt = 65,                          // VT_BLOB
                BlobSize = (uint)paramsSize,      // exactly sizeof(AUDIOCLIENT_ACTIVATION_PARAMS)
                BlobData = pParams,
            }, pPropVar, fDeleteOld: false);

            Guid iidAudioClient = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback, iidAudioClient, pPropVar, handler, out op);
            Marshal.ThrowExceptionForHR(hr);

            return await handler.Completion.ConfigureAwait(false);
        }
        finally
        {
            // Free only after the await returns - the native side read pParams during activation.
            Marshal.FreeHGlobal(pPropVar);
            Marshal.FreeHGlobal(pParams);
            GC.KeepAlive(handler);
            GC.KeepAlive(op);
        }
    }

    private static unsafe void InitializeFormat(IAudioClient client, ushort channels, int rate, ushort bits, bool autoConvert)
    {
        WAVEFORMATEX fmt = MakeFormat(channels, (uint)rate, bits);
        uint flags = PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK | PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (autoConvert) flags |= PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM;
        // Do NOT call GetMixFormat/IsFormatSupported on the loopback client (E_NOTIMPL).
        client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, BufferDurationHns, 0, &fmt, null);
    }

    private static WAVEFORMATEX MakeFormat(ushort channels, uint rate, ushort bits)
    {
        ushort blockAlign = (ushort)(channels * bits / 8);
        return new WAVEFORMATEX
        {
            wFormatTag = (ushort)(bits == 32 ? 3 : 1),   // 3 = WAVE_FORMAT_IEEE_FLOAT, 1 = WAVE_FORMAT_PCM
            nChannels = channels,
            nSamplesPerSec = rate,
            wBitsPerSample = bits,
            nBlockAlign = blockAlign,
            nAvgBytesPerSec = rate * blockAlign,
            cbSize = 0,
        };
    }

    private static unsafe IAudioCaptureClient GetCaptureClient(IAudioClient client)
    {
        Guid iid = typeof(IAudioCaptureClient).GUID;
        client.GetService(&iid, out object svc);
        return (IAudioCaptureClient)svc;
    }

    // --- capture pump ----------------------------------------------------------------

    private void PumpLoop()
    {
        while (_running)
        {
            try
            {
                if (!_bufferReady.WaitOne(200)) continue;   // event-driven; 200ms wake guards shutdown
                DrainPackets();
            }
            catch (Exception ex) when (IsInvalidation(ex))
            {
                if (!_running) break;
                Reactivate();
            }
            catch
            {
                if (!_running) break;   // benign teardown race; otherwise loop and retry on next event
            }
        }
    }

    private unsafe void DrainPackets()
    {
        IAudioCaptureClient capture = _capture!;
        capture.GetNextPacketSize(out uint packetFrames);
        while (packetFrames > 0 && _running)
        {
            byte* pData;
            uint frames, flags;
            ulong devicePos, qpcPos;
            capture.GetBuffer(&pData, out frames, out flags, &devicePos, &qpcPos);

            // Insert silence for any gap between what we've written and the device timeline.
            if (_anchorPos < 0) _anchorPos = (long)devicePos;     // anchor at first packet
            long pos = (long)devicePos - _anchorPos;
            long silence = SilenceGapFiller.SilenceFramesBefore(_writtenFrames, pos);
            if (silence > 0)
            {
                EmitStreamSilence(silence);
                _writtenFrames += silence;
            }

            bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
            if (silent || pData == null)
                EmitStreamSilence(frames);                        // honour SILENT flag (defensive)
            else
                EmitRealPacket(pData, frames);
            _writtenFrames += frames;

            capture.ReleaseBuffer(frames);
            capture.GetNextPacketSize(out packetFrames);
        }
    }

    /// <summary>Emit <paramref name="streamFrames"/> frames of silence in the initialized stream's
    /// units, converted to 16 kHz mono output (passes through the resampler in Option B so the
    /// 16 kHz timeline stays continuous).</summary>
    private void EmitStreamSilence(long streamFrames)
    {
        if (streamFrames <= 0) return;
        if (_mode == FormatMode.DirectMono16k)
            Emit(new float[streamFrames]);                        // already 16 kHz mono
        else
            Emit(_resampler!.Process(new float[streamFrames]));   // native-rate mono silence -> 16 kHz
    }

    private unsafe void EmitRealPacket(byte* pData, uint frames)
    {
        if (_mode == FormatMode.DirectMono16k)
        {
            int byteCount = (int)frames * 2;                      // 16-bit mono
            var bytes = new byte[byteCount];
            Marshal.Copy((IntPtr)pData, bytes, 0, byteCount);
            Emit(PcmConverter.Int16BytesToFloat(bytes));
        }
        else
        {
            int floatCount = (int)frames * _engineChannels;       // float32 interleaved
            var interleaved = new float[floatCount];
            Marshal.Copy((IntPtr)pData, interleaved, 0, floatCount);
            float[] mono = _engineChannels == 1 ? interleaved : PcmConverter.StereoToMono(interleaved);
            Emit(_resampler!.Process(mono));
        }
    }

    private void Emit(float[] mono16k)
    {
        if (mono16k.Length > 0)
            FrameAvailable?.Invoke(new AudioFrame(Source, _clock.ElapsedMs, mono16k));
    }

    private void Reactivate()
    {
        // Best-effort recovery (spike-grade): tear down and re-activate. Keep _writtenFrames and
        // re-anchor on the next packet so the stream appends continuously across the brief outage.
        ReleaseCom(ref _capture);
        ReleaseCom(ref _client);
        _anchorPos = -1;
        _resampler = null;
        if (!_running) return;
        ActivateAndInitialize();
    }

    // --- lifecycle -------------------------------------------------------------------

    public void Stop()
    {
        _running = false;
        _bufferReady.Set();
        _pump?.Join(1000);
        try { _client?.Stop(); } catch { /* already torn down */ }
    }

    public void Dispose()
    {
        Stop();
        ReleaseCom(ref _capture);
        ReleaseCom(ref _client);
        _bufferReady.Dispose();
    }

    private static void ReleaseCom<T>(ref T? com) where T : class
    {
        if (com is null) return;
        if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        com = null;
    }

    private static bool IsInvalidation(Exception ex)
        => ex.HResult == AUDCLNT_E_RESOURCES_INVALIDATED || ex.HResult == AUDCLNT_E_DEVICE_INVALIDATED;

    // --- hand-declared activation interop (CsWin32 cannot generate an implementable handler) -----

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlobHeader   // BLOB-only view of PROPVARIANT
    {
        public ushort Vt;          // VT_BLOB = 65
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;      // BLOB.cbSize
        public IntPtr BlobData;    // BLOB.pBlobData -> AUDIOCLIENT_ACTIVATION_PARAMS
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivateHandler : IActivateAudioInterfaceCompletionHandler
    {
        // RunContinuationsAsynchronously: do not run the rest of activation inline on the MTA callback thread.
        private readonly TaskCompletionSource<IAudioClient> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Completion => _tcs.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int hr = op.GetActivateResult(out int activateResult, out object activatedInterface);
                if (hr < 0) { _tcs.TrySetException(Marshal.GetExceptionForHR(hr)!); return 0; }
                if (activateResult < 0) { _tcs.TrySetException(Marshal.GetExceptionForHR(activateResult)!); return 0; }
                _tcs.TrySetResult((IAudioClient)activatedInterface);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            return 0;   // S_OK
        }
    }
}

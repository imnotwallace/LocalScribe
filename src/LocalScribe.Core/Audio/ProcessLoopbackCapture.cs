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
using LocalScribe.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace LocalScribe.Core.Audio;

public sealed class ProcessLoopbackCapture : ICaptureSource, IDiagnosticSource
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
    private volatile bool _shuttingDown;

    private FormatMode _mode;
    private int _engineRate = SampleRate;
    private int _engineChannels = 1;
    private MonoResampler16k? _resampler;   // Option B only

    // Gap-fill state. Frame counts are in the INITIALIZED stream's frame units
    // (16 kHz for Option A; native engine rate for Option B). devicePos is reported
    // in those same units, so SilenceGapFiller math is unit-consistent.
    private long _anchorPos = -1;
    private long _writtenFrames;

    // I-3 fix round 3 (review round 3, 2026-08-05): a WALL-CLOCK gate, not a packet count - a
    // counter cannot express "one line per episode" no matter which reset rule is chosen, and this
    // field has now proven that twice. REJECTED round 1's LIFETIME counter (never reset): after
    // the very first-ever discontinuity, a later, genuinely NEW, isolated episode only logged if
    // the cumulative total happened to land on a multiple of the old threshold - swallowed FOREVER
    // otherwise. REJECTED round 2's reset-on-clean-packet counter too: for an alternating
    // dirty/clean/dirty/clean pattern - a real device/driver hiccup shape, not a contrived one -
    // every dirty packet sees the counter freshly reset to 0 by the clean packet before it, so the
    // "first occurrence" branch fired on EVERY event, up to the ~100/second this flag can reach -
    // the exact flood this throttle exists to prevent, from the other direction. A time interval
    // bounds the rate under EVERY pattern - sustained, intermittent or isolated - and can never
    // permanently suppress a genuinely new episode, because time keeps moving regardless of packet
    // shape. See DiagnosticThrottleIntervalMs for the interval and its arithmetic.
    //
    // Environment.TickCount64, not IClock/TimeProvider: this is a LOG THROTTLE, not evidentiary
    // time recorded anywhere durable (session.json, transcripts, ...), so the repo's injected-clock
    // rule for evidentiary timestamps does not apply here - a monotonic, allocation-free tick count
    // is exactly the right tool, and threading TimeProvider through this call chain would buy
    // nothing a test could observe.
    //
    // Everything above is about the GATE - when a line is emitted. The COUNT carried inside that
    // line is a separate concern with a separate rule (F16); see _discontinuityCount below.
    private long? _lastDiscontinuityLogTicks;
    // CUMULATIVE since Start(), never reset anywhere (F16, final whole-branch review, 2026-08-05).
    // It used to count only the events since the last line actually logged, and was zeroed inside
    // the time gate right after logging - which silently DROPPED the tail of every episode: three
    // discontinuities inside two seconds during a 90-minute deposition produced exactly ONE line,
    // claiming a count of 1, and the other two silence insertions into an evidentiary recording
    // were recorded nowhere at all. A support engineer reads that line and concludes a single blip.
    // A running total cannot lose a tail, because whichever line is emitted next carries every
    // event that came before it. No reset at Start() either: a ProcessLoopbackCapture is
    // constructed per capture leg per session, so field-initial zero already IS "since Start()",
    // and a reset would add a second zeroing site - which is exactly what the pinned occurrence
    // count in ProcessLoopbackCaptureSourceTests forbids. Deliberately NOT reset at the
    // DropClient/stream boundary either - see DropClient's own comment for why boundary resets are
    // wrong here.
    private long _discontinuityCount;

    // I-3 fix round 3 (review round 3, 2026-08-05): shared by the discontinuity gate above and the
    // pump-loop fault gate in PumpLoop's catch block below - both throttles were found to have the
    // SAME counter-based flaw (see each field's doc comment) and are fixed the SAME way. At this
    // flag's own stated worst case (~100 discontinuities/second): 30_000ms yields at most one line
    // per 3,000 events, i.e. 3600/30 = 120 lines/hour, i.e. at most 360 lines across a 3-hour
    // recording - versus well over a MILLION unthrottled, or a full-rate flood under an
    // intermittent pattern (round 2's actual bug). At the pump-loop's own worst case (~1
    // fault/second, capped by the backoff below): the same 120 lines/hour, 360 lines/3 hours -
    // versus the ~10,800 unthrottled figure from round 1's own comment, or the same full-rate
    // flood under an intermittent success/fail pattern (see the comment in PumpLoop's catch block
    // where this constant is used). 30 seconds sits inside the 10-60s range that keeps "still
    // happening" visible on a human support timescale without flooding a file that is never pruned.
    private const long DiagnosticThrottleIntervalMs = 30_000;

    /// <summary>Diagnostics for the smoke test: which format mode/engine rate won, and the activation param size.</summary>
    public string ActivationInfo { get; private set; } = "(not started)";

    // I-2 fix round 2 (review round 2, 2026-08-05): the last ActivationInfo string actually
    // logged, or null before the first activation. See the Diag("activated: ...") call below for
    // why this exists.
    private string? _lastLoggedActivationInfo;

    public SourceKind Source => SourceKind.Remote;
    public event Action<AudioFrame>? FrameAvailable;

    /// <summary>Best-effort diagnostics (activation fallback, recovery, capture errors). Subscribed
    /// by SpikeRunner and - since Tier 1 plan A, 2026-08-05 - by the app's diagnostic log, via
    /// WasapiCaptureSourceProvider's sink.
    ///
    /// The message PREFIX is this event's severity vocabulary and is load-bearing across files
    /// (F3, final whole-branch review, 2026-08-05): CompositionRoot.CaptureDiagnosticLevel maps
    /// "capture error" and "device invalidated" to error, "data discontinuity" to warn and
    /// everything else ("activated: ...") to info. Renaming or re-wording a prefix silently
    /// downgrades a capture FAULT to info, where it can never latch DiagnosticLog.LastError and so
    /// never reaches Settings' "Copy last error". Both sides are pinned - see
    /// ProcessLoopbackCaptureSourceTests.Diagnostic_message_prefixes_are_the_severity_vocabulary_the_app_sink_maps.</summary>
    public event Action<string>? Diagnostic;

    /// <summary>F10 (final whole-branch review, 2026-08-05): SWALLOWS a subscriber fault, because
    /// one of the two Diag call sites lives INSIDE PumpLoop's catch block, whose own comment states
    /// the invariant - "Recovery must NEVER throw out of the loop - that would kill the pump thread
    /// and with it WavSink.Dispose, corrupting both recordings". Diagnostic is a public event
    /// invoking arbitrary subscriber code, so without this guard a throwing subscriber escapes that
    /// catch, terminates the pump thread and (an unhandled exception on a background thread) takes
    /// the process down mid-recording. Safe TODAY only because the single app subscriber happens to
    /// be guarded (DiagnosticLog.Write never throws) - but this round is what first attached app
    /// code to this event at all, CaptureDiagnostics.Attach is public API that Plans B/C/D may use,
    /// and F3 just changed what that subscriber does. A diagnostic is never worth a recording.</summary>
    private void Diag(string message)
    {
        try { Diagnostic?.Invoke(message); }
        catch { /* see the doc comment: a subscriber fault must never reach the pump loop */ }
    }

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
                if (_shuttingDown) break;   // stay responsive to teardown during re-establishment
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
            {
                if (_shuttingDown) return;
                throw new InvalidOperationException(
                    "Process loopback Initialize failed for Option A (16 kHz AUTOCONVERTPCM) and all " +
                    "Option B native-format candidates. Last error: " + (_lastError?.Message ?? "unknown") +
                    " (pid " + _targetPid + ", excludeMode " + _excludeMode + ").", _lastError);
            }
        }

        _capture = GetCaptureClient(_client);
        _client.SetEventHandle(new HANDLE(_bufferReady.SafeWaitHandle.DangerousGetHandle()));
        _client.Start();

        ActivationInfo = $"mode={_mode}, engineRate={_engineRate}, engineCh={_engineChannels}, " +
                         $"paramsSize={paramsSize}, pid={_targetPid}, excludeMode={_excludeMode}";
        // I-2 fix (review round 1, 2026-08-05): the ONLY place that reports which format path won -
        // Option A (DirectMono16k) or the degraded Option B (NativeResample) software fallback.
        // IDiagnosticSource's doc comment, this event's doc comment and CompositionRoot's wiring
        // comment all promise "activation fallback" visibility; without this line nothing ever
        // emitted one, so a support engineer reading the log and finding no fallback line would
        // wrongly conclude none occurred, when Option B is exactly the degraded-quality path worth
        // knowing about. ActivationInfo is fixed vocabulary (_mode is an enum) plus integers and a
        // numeric process id - none of it is an identifier - so unlike the free-text exception
        // messages elsewhere in this class, it needs no DiagnosticRedaction.Mark.
        //
        // I-2 fix round 2 (review round 2, 2026-08-05): REJECTED round 1's unconditional Diag()
        // here - ActivateAndInitialize also runs on every pump-loop RE-activation (":286,
        // (re)establish after a drop"), so a persistent post-activation fault re-activated and
        // re-emitted this line roughly once a second, reintroducing the exact flood I-3 exists to
        // close, through a door I-3 never touched. REJECTED a bare count gate too (the I-3 shape):
        // the transition a support engineer actually needs to see - Option A falling back to B, or
        // recovering back to A - could land between ticks of a count gate and never be logged.
        // Comparing against the LAST LOGGED value instead: the first activation of a session always
        // logs (the field starts null), every later activation logs ONLY when the format actually
        // changed, and a persistent fault that keeps landing on the same format stays silent.
        if (_lastLoggedActivationInfo != ActivationInfo)
        {
            Diag("activated: " + ActivationInfo);
            _lastLoggedActivationInfo = ActivationInfo;
        }
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
        // AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY (0x08000000) is deliberately NOT set: it is optional
        // (the decisions doc pairs it with AUTOCONVERTPCM, but NAudio PR #1348 reports it unsupported on
        // VAD\Process_Loopback). Omitting it maximises the chance Option A's Initialize succeeds; it is a
        // box-verify item to try separately if Option A wins and converter quality needs improving.
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
        int errors = 0;
        // I-3 fix round 3 (review round 3, 2026-08-05): local, not a field, because it only needs
        // to persist across iterations of THIS while loop, exactly like `errors` above - a fresh
        // PumpLoop call (a fresh Start()) starts a fresh throttle window. See its use below for why
        // this replaced the `errors == 0 || errors % 60 == 0` gate: TRACED per the coordinator's
        // request - `errors = 0` runs after ANY successful try-block pass, including a bare
        // successful ActivateAndInitialize() with no exception yet from the DrainPackets that
        // follows. A flaky endpoint that alternates "reactivates fine" / "fails again immediately" -
        // a real shape, e.g. a render process that keeps restarting - resets `errors` to 0 before
        // every single failure, so `errors == 0` fired on EVERY fault, not just the first: the same
        // packet-parity flaw as _discontinuityCount's round-2 attempt, just from the success side
        // instead of the clean-packet side. A counter reset by ANY intervening success cannot gate
        // a log line correctly no matter which reset rule is chosen; only wall-clock time can.
        long? lastFaultLogTicks = null;
        while (_running)
        {
            try
            {
                if (_capture is null)
                    ActivateAndInitialize();                 // (re)establish after a drop; throws on failure
                else if (_bufferReady.WaitOne(200))          // event-driven; 200ms wake guards shutdown
                    DrainPackets();
                errors = 0;
            }
            catch (Exception ex)
            {
                if (!_running) break;
                // Recovery must NEVER throw out of the loop - that would kill the pump thread and with it
                // WavSink.Dispose, corrupting both recordings (the whole smoke-test deliverable). Log, drop
                // the client so the next iteration re-activates, and back off so a persistent error cannot hot-loop.
                //
                // I-1 fix (review round 1, 2026-08-05): ex.Message is free text from an arbitrary
                // exception - a COM error description today, but this catch also wraps
                // ActivateAndInitialize, whose own InvalidOperationException message can embed a
                // FrameAvailable subscriber's fault (SpikeRunner/Program.cs:200 already attaches a
                // disk-writing sink to that event) - so it is marked, per DiagnosticRedaction.
                // ForException's own rule: "every MESSAGE marked". Marking also NEUTRALISES any "<<"
                // the message happens to contain (COM/native messages quote template or XML
                // fragments), which is what stops it from tripping Apply()'s fail-closed
                // unterminated-marker path and eating the HRESULT and "- recovering" that follow.
                // The classification and HRESULT stay unmarked: both are fixed vocabulary /
                // numeric, never identifying, and are exactly the signal this diagnostic exists for.
                //
                // I-3 fix round 3 (review round 3, 2026-08-05): a WALL-CLOCK gate - see
                // lastFaultLogTicks's doc comment above for the traced intermittent-fault hole this
                // replaces (round 1's `errors == 0 || errors % 60 == 0`), and
                // DiagnosticThrottleIntervalMs's doc comment for the interval and its arithmetic
                // (same 30s constant this shares with the discontinuity gate in DrainPackets - at
                // this site's own ~1/second worst case, at most 360 lines across a 3-hour
                // recording). errors is UNCHANGED below and still drives the backoff sleep only.
                long now = Environment.TickCount64;
                if (lastFaultLogTicks is null || now - lastFaultLogTicks.Value >= DiagnosticThrottleIntervalMs)
                {
                    Diag((IsInvalidation(ex) ? "device invalidated" : "capture error") +
                         " (0x" + ((uint)ex.HResult).ToString("X8") + "): " +
                         DiagnosticRedaction.Mark(ex.Message) + " - recovering");
                    lastFaultLogTicks = now;
                }
                DropClient();
                if (++errors > 1) Thread.Sleep(Math.Min(1000, 150 * (errors - 1)));
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
            ulong devicePos, qpcPos;   // qpcPos: documented QPC-delta gap-fill fallback hook (device position is primary)
            capture.GetBuffer(&pData, out frames, out flags, &devicePos, &qpcPos);
            try
            {
                // Insert silence for any gap between what we've written and the device timeline.
                if (_anchorPos < 0) _anchorPos = (long)devicePos;     // anchor at first packet
                long pos = (long)devicePos - _anchorPos;
                long silence = SilenceGapFiller.SilenceFramesBefore(_writtenFrames, pos);
                if (silence > 0)
                {
                    EmitStreamSilence(silence);
                    _writtenFrames += silence;
                }
                if ((flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                {
                    // I-3 fix round 3 (review round 3, 2026-08-05): see _lastDiscontinuityLogTicks's
                    // doc comment for why this is a wall-clock gate rather than a packet count (two
                    // prior count-based attempts each failed on a different, real packet pattern).
                    _discontinuityCount++;
                    long now = Environment.TickCount64;
                    if (_lastDiscontinuityLogTicks is null ||
                        now - _lastDiscontinuityLogTicks.Value >= DiagnosticThrottleIntervalMs)
                    {
                        // The count is a running TOTAL and is NOT zeroed here (F16) - see
                        // _discontinuityCount's doc comment for the episode-tail loss that zeroing
                        // caused. The "data discontinuity" prefix is the app sink's severity
                        // vocabulary (F3) - see the Diagnostic event's doc comment.
                        Diag("data discontinuity at devicePos " + devicePos + " - inserted " + silence +
                             " silence frames (" + _discontinuityCount + " total)");
                        _lastDiscontinuityLogTicks = now;
                    }
                }

                bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                if (silent || pData == null)
                    EmitStreamSilence(frames);                        // honour SILENT flag (defensive)
                else
                    EmitRealPacket(pData, frames);
                _writtenFrames += frames;
            }
            finally
            {
                capture.ReleaseBuffer(frames);                        // always return the borrowed buffer
            }
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

    private void DropClient()
    {
        try { _client?.Stop(); } catch { /* already gone */ }
        ReleaseCom(ref _capture);
        ReleaseCom(ref _client);
        _resampler = null;
        // Reset the stream-local timeline as a UNIT. The re-activated client restarts its device position,
        // so carrying _writtenFrames forward would pin the gap-fill delta and silently disable gap-fill for
        // the rest of the session. Resetting accepts a one-time, bounded loss of the outage duration instead.
        _anchorPos = -1;
        _writtenFrames = 0;
        // I-3 fix round 3 (review round 3, 2026-08-05): round 2 reset _discontinuityCount here as a
        // reconnect-boundary belt-and-braces for the then-count-based throttle. That throttle is now
        // wall-clock (see _lastDiscontinuityLogTicks's doc comment), which needs no boundary reset at
        // all - deliberately NOT resetting _lastDiscontinuityLogTicks here means a reconnect storm
        // cannot force an immediate re-log burst either, which a reset would have reintroduced.
    }

    // --- lifecycle -------------------------------------------------------------------

    public void Stop()
    {
        _shuttingDown = true;
        _running = false;
        _bufferReady.Set();
        Thread? pump = _pump;
        if (pump != null)
        {
            // Wait until the pump has fully exited before any COM release, re-signalling so it wakes from
            // WaitOne. Closes the race where FinalReleaseComObject could hit an object the pump is still
            // using (e.g. mid re-activation), which Join(1000) could otherwise be outrun by.
            while (!pump.Join(200)) _bufferReady.Set();
            _pump = null;
        }
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

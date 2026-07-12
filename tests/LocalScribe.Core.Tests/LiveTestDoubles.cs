using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

namespace LocalScribe.Core.Tests;

/// <summary>Deterministic VAD stand-in: speech prob 1.0 when any sample in the window is
/// non-zero, else 0.0. Lets tests drive VadCore with amplitude-shaped fake frames.</summary>
public sealed class AmplitudeSpeechModel : ISpeechProbabilityModel
{
    public float SpeechProbability(ReadOnlySpan<float> window)
    {
        for (int i = 0; i < window.Length; i++)
            if (window[i] != 0f) return 1f;
        return 0f;
    }
    public void Reset() { }
}

/// <summary>IEngineFactory over the existing FakeTranscriptionEngine. Promoted from
/// TranscriptionWorkerTests' former ScriptedFactory (renamed here so 2b and Stage-3a tests
/// share one fake instead of two near-duplicates): any per-plan engine construction is still
/// supported via the Func&lt;BackendPlan, ITranscriptionEngine&gt; constructor, and the
/// parameterless/transcribe-func constructor adds a default that echoes segment identity so
/// LiveSourcePipeline assertions can tie output lines back to input audio.</summary>
public sealed class FakeEngineFactory : IEngineFactory
{
    public readonly List<(BackendPlan Plan, string? Language)> Created = new();
    private readonly Func<BackendPlan, ITranscriptionEngine> _make;
    public int CreateCalls => Created.Count;
    public string? LastInitialPrompt;

    public FakeEngineFactory(Func<AudioSegment, TranscriptionResult>? transcribe = null)
        : this(plan => new FakeTranscriptionEngine(plan.ModelName, transcribe ?? (s =>
            new TranscriptionResult($"{s.Source} {s.StartMs}-{s.EndMs}", "en", 0.0))))
    {
    }

    public FakeEngineFactory(Func<BackendPlan, ITranscriptionEngine> make) => _make = make;

    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language,
        string? initialPrompt, CancellationToken ct)
    {
        Created.Add((plan, language));
        LastInitialPrompt = initialPrompt;
        return Task.FromResult(_make(plan));
    }
}

/// <summary>Engine factory whose CreateAsync blocks SYNCHRONOUSLY until CreateGate is set, then
/// returns a completed task - mirrors the real WhisperEngineFactory (Task.FromResult(new
/// WhisperNetEngine(...)) where the ctor loads the model synchronously). With a direct
/// `worker.RunAsync(...)` call this blocks StartAsync exactly as production does today; wrapping the
/// worker start in Task.Run (Task 1) unblocks it. Also lets Stop-path tests hold the drain open
/// (Phase 2).</summary>
internal sealed class GatedEngineFactory : IEngineFactory
{
    public readonly ManualResetEventSlim CreateGate = new(initialState: false);
    public int CreateCalls;
    private readonly Func<BackendPlan, ITranscriptionEngine> _make;

    public GatedEngineFactory(Func<AudioSegment, TranscriptionResult>? transcribe = null)
        => _make = plan => new FakeTranscriptionEngine(plan.ModelName, transcribe ?? (s =>
            new TranscriptionResult($"{s.Source} {s.StartMs}-{s.EndMs}", "en", 0.0)));

    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language,
        string? initialPrompt, CancellationToken ct)
    {
        Interlocked.Increment(ref CreateCalls);
        CreateGate.Wait(ct);                                  // SYNCHRONOUS block, like the real model load
        return Task.FromResult<ITranscriptionEngine>(_make(plan));
    }
}

/// <summary>Wraps a real capture source and records whether Dispose was called - lets Start-path
/// and Stop-path tests assert that a partial failure never leaks a live capture handle.</summary>
internal sealed class DisposalTrackingSource : ICaptureSource, IEndpointMuteObservable
{
    private readonly ICaptureSource _inner;
    public bool Disposed { get; private set; }
    public DisposalTrackingSource(ICaptureSource inner) => _inner = inner;
    public SourceKind Source => _inner.Source;
    public event Action<AudioFrame>? FrameAvailable
    { add => _inner.FrameAvailable += value; remove => _inner.FrameAvailable -= value; }
    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();
    public void Dispose() { Disposed = true; _inner.Dispose(); }

    // Forwards IEndpointMuteObservable when the wrapped source implements it (Task 3, design
    // 2026-07-10 section 2); a non-observable inner source (e.g. StopThrowingSource) means the
    // controller's type-test sees "no awareness" - the fail-open contract.
    public bool DeviceMuted => (_inner as IEndpointMuteObservable)?.DeviceMuted ?? false;
    public event Action<bool>? DeviceMuteChanged
    {
        add { if (_inner is IEndpointMuteObservable m) m.DeviceMuteChanged += value; }
        remove { if (_inner is IEndpointMuteObservable m) m.DeviceMuteChanged -= value; }
    }
}

/// <summary>Wraps a real capture source whose Stop() throws - simulates a genuine (non-cancellation)
/// leg fault during Stop/Pause so SessionController's fault-precedence guards can be exercised.</summary>
internal sealed class StopThrowingSource : ICaptureSource
{
    private readonly ICaptureSource _inner;
    public StopThrowingSource(ICaptureSource inner) => _inner = inner;
    public SourceKind Source => _inner.Source;
    public event Action<AudioFrame>? FrameAvailable
    { add => _inner.FrameAvailable += value; remove => _inner.FrameAvailable -= value; }
    public void Start() => _inner.Start();
    public void Stop() => throw new IOException("stop failed");
    public void Dispose() => _inner.Dispose();
}

/// <summary>Fake ICaptureSourceProvider: hands out FakeCaptureSource legs and records how many
/// times each side was created (fresh legs on Resume, probe throwaways on preflight) plus lets
/// tests inject one-shot failures and disposal-tracking on the most recently created sources.</summary>
internal sealed class FakeProvider : ICaptureSourceProvider
{
    private static float[][] SpeechThenSilence(int speech, int silence)
    {
        var frames = new List<float[]>();
        for (int i = 0; i < speech; i++) frames.Add(Enumerable.Repeat(0.5f, 512).ToArray());
        for (int i = 0; i < silence; i++) frames.Add(new float[512]);
        return frames.ToArray();
    }

    public Func<float[][]> LocalFrames = () => SpeechThenSilence(4, 3);
    public Func<float[][]> RemoteFrames = () => SpeechThenSilence(4, 3);
    public RemoteSnapshot RemoteSnapshot = new()
    { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost", FellBackToSystemMix = false };
    public MicSnapshot MicSnapshot = new() { Mode = MicMode.FollowDefault, Name = "Fake Mic" };

    // Explicit-target overload (design 2026-07-12): resolves the honest RemoteSnapshot through the
    // real planner over this active-session list, so SetRemoteCaptureAsync marker tests are truthful.
    public List<AudioSessionInfo> ActiveSessions = new()
    { new AudioSessionInfo(4242, "CiscoCollabHost"), new AudioSessionInfo(5151, "Zoom") };
    public int MicCreates, RemoteCreates;
    public bool ThrowOnNextRemoteCreate;                 // one-shot: cleared when it fires
    public bool ThrowOnNextMicCreate;                    // one-shot: cleared when it fires (2026-07-11 review fix)
    public bool ThrowOnLocalStop;                        // local leg faults genuinely at Stop()
    public DisposalTrackingSource? LastMic, LastRemote;
    public bool NextMicDeviceMuted = false;               // seeds the fake's initial DeviceMuted (Task 4 sets this)
    public FakeCaptureSource? LastMicFake;                // unwrapped fake, so tests can raise device-mute

    public (ICaptureSource, MicSnapshot) CreateMic(IClock clock)
    {
      if (ThrowOnNextMicCreate)
      { ThrowOnNextMicCreate = false; throw new InvalidOperationException("mic gone"); }
      MicCreates++;
      var fake = new FakeCaptureSource(SourceKind.Local, LocalFrames()) { DeviceMuted = NextMicDeviceMuted };
      LastMicFake = fake;
      ICaptureSource src = fake;
      if (ThrowOnLocalStop) src = new StopThrowingSource(src);
      LastMic = new DisposalTrackingSource(src);
      return (LastMic, MicSnapshot); }

    public (ICaptureSource, RemoteSnapshot) CreateRemote(IClock clock)
    { RemoteCreates++;
      if (ThrowOnNextRemoteCreate)
      { ThrowOnNextRemoteCreate = false; throw new InvalidOperationException("remote capture unavailable"); }
      LastRemote = new DisposalTrackingSource(new FakeCaptureSource(SourceKind.Remote, RemoteFrames()));
      return (LastRemote, RemoteSnapshot); }

    public (ICaptureSource, RemoteSnapshot) CreateRemote(IClock clock, RemoteSetting setting)
    { RemoteCreates++;
      if (ThrowOnNextRemoteCreate)
      { ThrowOnNextRemoteCreate = false; throw new InvalidOperationException("remote capture unavailable"); }
      var plan = RemoteCapturePlanner.Plan(ActiveSessions, setting);
      LastRemote = new DisposalTrackingSource(new FakeCaptureSource(SourceKind.Remote, RemoteFrames()));
      return (LastRemote, new RemoteSnapshot
      { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix }); }
}

/// <summary>Shared SessionController test wiring (Task 8's MakeController/Options, promoted here
/// so SessionControllerTests and SessionControllerPauseTests share one copy). ProbeWindow is
/// forced to a few milliseconds by Options() so preflight-probe tests stay fast - see
/// LiveSessionOptions.ProbeWindow doc for why the probe otherwise costs 1 real second per side.</summary>
internal static class LiveTestDoubles
{
    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    /// <summary>Default model-presence stub for the Start fail-fast (Task 3): matches what the
    /// default auto-plan resolves to on this fake probe (StaticHardwareProbe(false,0,false,4) ->
    /// backend Cpu, fastCores 4 -> ceiling base.en). Injected via SessionController's
    /// Func&lt;IReadOnlySet&lt;string&gt;&gt; seam - deliberately NOT the LOCALSCRIBE_MODELS env
    /// var, which is process-global and would race across xUnit's parallel test classes; tests
    /// that need to exercise "model absent"/"model downgraded" pass their own set instead.</summary>
    private static readonly IReadOnlySet<string> DefaultAvailableModels =
        new HashSet<string> { "base.en", "tiny.en" };

    internal static (SessionController Controller, FakeProvider Provider, StoragePaths Paths, FakeClock Clock)
        MakeController(string root, Settings? settings = null, IEngineFactory? engineFactory = null,
            IReadOnlySet<string>? availableModels = null)
    {
        settings ??= new Settings();
        var paths = new StoragePaths(root);
        var provider = new FakeProvider();
        var clock = new FakeClock();
        var models = availableModels ?? DefaultAvailableModels;
        var controller = new SessionController(paths, settings, engineFactory ?? new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            provider, () => clock, new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero)),
            "0.3.0", () => models);
        return (controller, provider, paths, clock);
    }

    internal static LiveSessionOptions Options() => new()
    { App = AppKind.Webex, Vad = TestVad, RunPreflightProbe = false, ProbeWindow = TimeSpan.FromMilliseconds(20) };
}

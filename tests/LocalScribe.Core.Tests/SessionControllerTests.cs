using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-live-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    private static float[][] SpeechThenSilence(int speech, int silence)
    {
        var frames = new List<float[]>();
        for (int i = 0; i < speech; i++) frames.Add(Enumerable.Repeat(0.5f, 512).ToArray());
        for (int i = 0; i < silence; i++) frames.Add(new float[512]);
        return frames.ToArray();
    }

    private sealed class FakeProvider : ICaptureSourceProvider
    {
        public Func<float[][]> LocalFrames = () => SpeechThenSilence(4, 3);
        public Func<float[][]> RemoteFrames = () => SpeechThenSilence(4, 3);
        public RemoteSnapshot RemoteSnapshot = new()
        { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost", FellBackToSystemMix = false };
        public int MicCreates, RemoteCreates;

        public (ICaptureSource, MicSnapshot) CreateMic(IClock clock)
        { MicCreates++; return (new FakeCaptureSource(SourceKind.Local, LocalFrames()),
            new MicSnapshot { Mode = MicMode.FollowDefault, Name = "Fake Mic" }); }

        public (ICaptureSource, RemoteSnapshot) CreateRemote(IClock clock)
        { RemoteCreates++; return (new FakeCaptureSource(SourceKind.Remote, RemoteFrames()), RemoteSnapshot); }
    }

    private (SessionController Controller, FakeProvider Provider, StoragePaths Paths, FakeClock Clock)
        MakeController(Settings? settings = null)
    {
        settings ??= new Settings();
        var paths = new StoragePaths(_root);
        var provider = new FakeProvider();
        var clock = new FakeClock();
        var controller = new SessionController(paths, settings, new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            provider, () => clock, new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero)),
            "0.3.0");
        return (controller, provider, paths, clock);
    }

    private static LiveSessionOptions Options() => new()
    { App = AppKind.Webex, Vad = TestVad, RunPreflightProbe = false };

    [Fact]
    public async Task Start_then_stop_produces_finalized_session_folder()
    {
        var (c, _, paths, clock) = MakeController();
        var states = new List<SessionState>();
        c.StateChanged += s => states.Add(s);

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.Equal(id, c.CurrentSessionId);

        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None);
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Null(c.CurrentSessionId);
        Assert.Equal([SessionState.Recording, SessionState.Finalizing, SessionState.Idle], states);

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);
        Assert.Equal(5000, record.DurationMs);              // wall clock, not max segment end
        Assert.Equal(2, record.SegmentCount);               // one per source
        Assert.Equal(AppKind.Webex, record.App);
        Assert.Equal("Fake Mic", record.Devices.Mic.Name);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], record.RetainedAudioSources);
        Assert.True(File.Exists(paths.TranscriptMd(id!)));
        Assert.True(File.Exists(paths.SessionTxt(id!)));
        Assert.True(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        Assert.True(File.Exists(paths.AudioFile(id!, SourceKind.Remote, AudioFormat.Flac)));
    }

    [Fact]
    public async Task Lines_flow_to_LineInserted_and_transcript_jsonl()
    {
        var (c, _, paths, _) = MakeController();
        var lines = new List<TranscriptLine>();
        c.LineInserted += (_, l) => { lock (lines) lines.Add(l); };

        string? id = await c.StartAsync(Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Segment));
        Assert.Contains(lines, l => l.Source == TranscriptSource.Local && l.SpeakerLabel == "Me");
        Assert.Contains(lines, l => l.Source == TranscriptSource.Remote && l.SpeakerLabel == "Them");
        var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, stored.Count(l => l.Kind == TranscriptKind.Segment));
    }

    [Fact]
    public async Task Retention_never_skips_audio_files()
    {
        var (c, _, paths, _) = MakeController(new Settings { AudioRetention = "never" });
        string? id = await c.StartAsync(Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Empty(record!.RetainedAudioSources);
    }

    [Fact]
    public async Task Second_start_is_ignored_with_notice()
    {
        var (c, provider, _, _) = MakeController();
        string? notice = null;
        c.Notice += n => notice = n;

        string? first = await c.StartAsync(Options(), CancellationToken.None);
        string? second = await c.StartAsync(Options(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);                                 // single-session guard (design 5)
        Assert.NotNull(notice);
        Assert.Equal(1, provider.MicCreates);
        await c.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_when_idle_is_ignored_with_notice()
    {
        var (c, _, _, _) = MakeController();
        string? notice = null;
        c.Notice += n => notice = n;
        Assert.Null(await c.StopAsync(CancellationToken.None));
        Assert.NotNull(notice);
        Assert.Equal(SessionState.Idle, c.State);
    }
}

using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

public sealed class RetranscriptionRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero));
    public RetranscriptionRunnerTests()
    { _paths = new StoragePaths(Path.Combine(_root, "store")); Directory.CreateDirectory(_root); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly VadOptions TestVad = new()
    { Threshold = 0.5f, MinSpeechMs = 64, MinSilenceMs = 64, SpeechPadMs = 0, MaxSegmentMs = 15000 };

    private static void WriteBurstWav(string path, params (int SilenceMs, int SpeechMs)[] pattern)
    {
        using var sink = new WavSink(path);
        foreach (var (silence, speech) in pattern)
        {
            sink.Write(new float[16 * silence]);
            var burst = new float[16 * speech];
            for (int i = 0; i < burst.Length; i++)
                burst[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / 16000.0));
            sink.Write(burst);
        }
        sink.Write(new float[16 * 1000]);                          // trailing second of silence
    }

    /// <summary>A finalized session with one retained WAV leg (FlacPcmReader reads .wav too, so
    /// no FLAC fixture dependency) and a root machine transcript of ONE segment.</summary>
    private async Task<string> SeedFinalizedAsync(string id = "2026-07-10_1000_Webex_seed", bool ended = true)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = ended ? new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero) : null,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
            Sources = [SourceKind.Local], RetainedAudioSources = [SourceKind.Local],
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Original words.", "Me"), default);
        WriteBurstWav(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav), (200, 1500));
        return id;
    }

    private RetranscriptionRunner MakeRunner(Settings? settings = null, IEngineFactory? engine = null,
        Func<string?>? liveBusy = null,
        Func<string, Func<CancellationToken, Task>, Task>? runUnderGate = null)
        => new(_paths, () => settings ?? new Settings(), engine ?? new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), _time, liveBusy ?? (() => null),
            () => new HashSet<string> { "base.en", "tiny.en" }, runUnderGate);

    private RetranscriptionRequest Request(string id, string model = "base.en")
        => new() { SessionId = id, Model = model, Language = "en", Vad = TestVad };

    [Fact]
    public async Task Run_creates_v2_with_fresh_edits_flips_activeVersion_and_never_touches_root_content()
    {
        string id = await SeedFinalizedAsync();
        byte[] rootJsonl = await File.ReadAllBytesAsync(_paths.TranscriptJsonl(id));
        var runner = MakeRunner();
        var completed = new List<string>();
        runner.RetranscriptionCompleted += cid => { lock (completed) completed.Add(cid); };

        string? vid = await runner.RunAsync(Request(id), CancellationToken.None);

        string expected = TranscriptVersions.NewId(2, "base.en", DateOnly.FromDateTime(_time.GetLocalNow().Date));
        Assert.Equal(expected, vid);
        Assert.Null(runner.RunningSessionId);
        Assert.Equal(new[] { id }, completed.ToArray());

        // Version folder: its own machine transcript (>= 1 segment from the burst), a fresh
        // EMPTY edits.json, rendered projections with the version's header actuals.
        var vLines = await new TranscriptStore(_paths.TranscriptJsonl(id, vid!)).ReadAllAsync(default);
        Assert.Contains(vLines, l => l.Kind == TranscriptKind.Segment);
        var vEdits = await new EditStore(_paths.SessionDir(id), TimeProvider.System,
            contentDir: _paths.VersionDir(id, vid!)).LoadAsync(default);
        Assert.NotNull(vEdits);
        Assert.Empty(vEdits!.Corrections);
        Assert.False(File.Exists(_paths.SpeakersJson(id, vid!)));      // absent until Split
        Assert.Contains("base.en/CPU", await File.ReadAllTextAsync(_paths.TranscriptMd(id, vid!)));

        // session.json commit: entry + flip; root truth fields still describe v1.
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(vid, session!.ActiveVersion);
        var entry = Assert.Single(session.Versions);
        Assert.Equal("base.en", entry.Model);
        Assert.Equal("ggml-base.en.bin", entry.WeightsFile);    // FakeTranscriptionEngine's default file
        Assert.Equal("CPU", entry.Backend);
        Assert.Equal("en", entry.Language);
        Assert.False(entry.VocabularyApplied);                          // no vocabulary configured
        Assert.Equal(_time.GetUtcNow(), entry.CreatedAtUtc);
        Assert.Equal("small.en", session.Model);                        // root record untouched

        // Evidentiary: the v1 machine transcript is bit-for-bit untouched; no root edits appear.
        Assert.Equal(rootJsonl, await File.ReadAllBytesAsync(_paths.TranscriptJsonl(id)));
        Assert.False(File.Exists(_paths.EditsJson(id)));
    }

    [Fact]
    public async Task Second_run_is_monotonic_v3_and_appends_to_versions()
    {
        string id = await SeedFinalizedAsync();
        var runner = MakeRunner();
        string? v2 = await runner.RunAsync(Request(id), CancellationToken.None);
        string? v3 = await runner.RunAsync(Request(id, model: "tiny.en"), CancellationToken.None);

        Assert.StartsWith("v2-", v2);
        Assert.StartsWith("v3-tiny.en-", v3);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(v3, session!.ActiveVersion);
        Assert.Equal(new[] { v2, v3 }, session.Versions.Select(v => v.Id).ToArray());
    }

    [Fact]
    public async Task Refuses_unfinalized_sessions_a_busy_live_engine_and_a_missing_model()
    {
        var notices = new List<string>();

        string pending = await SeedFinalizedAsync("2026-07-10_1100_Webex_pending", ended: false);
        var runner = MakeRunner();
        runner.Notice += n => { lock (notices) notices.Add(n); };
        Assert.Null(await runner.RunAsync(Request(pending), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("finalized"));
        Assert.False(Directory.Exists(_paths.VersionsDir(pending)));

        string id = await SeedFinalizedAsync();
        var busyRunner = MakeRunner(liveBusy: () => "A recording is in progress - stop it first.");
        busyRunner.Notice += n => { lock (notices) notices.Add(n); };
        Assert.Null(await busyRunner.RunAsync(Request(id), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("recording is in progress"));

        var runner2 = MakeRunner();
        runner2.Notice += n => { lock (notices) notices.Add(n); };
        Assert.Null(await runner2.RunAsync(Request(id, model: "large-v3"), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("not downloaded"));
        Assert.False(Directory.Exists(_paths.VersionsDir(id)));        // no folder from any refusal
    }

    [Fact]
    public async Task One_run_at_a_time_and_cancel_discards_only_the_partial_folder()
    {
        string id = await SeedFinalizedAsync();
        var gated = new GatedEngineFactory();
        var runner = MakeRunner(engine: gated);
        var notices = new List<string>();
        runner.Notice += n => { lock (notices) notices.Add(n); };

        var run = runner.RunAsync(Request(id), CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => runner.RunningSessionId == id, TimeSpan.FromSeconds(10)));
        Assert.True(SpinWait.SpinUntil(() => Directory.Exists(_paths.VersionsDir(id)), TimeSpan.FromSeconds(10)));

        // Second concurrent run refuses (one re-transcription at a time).
        Assert.Null(await runner.RunAsync(Request(id), CancellationToken.None));
        Assert.Contains(notices, n => n.Contains("already running"));

        runner.CancelCurrent();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(15)));
        gated.CreateGate.Set();                                        // release the parked engine build

        // The partial folder is discarded; session.json never gained an entry; the slot is free.
        Assert.False(Directory.Exists(_paths.VersionDir(id,
            TranscriptVersions.NewId(2, "base.en", DateOnly.FromDateTime(_time.GetLocalNow().Date)))));
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);
        Assert.Empty(session.Versions);
        Assert.Null(runner.RunningSessionId);
    }

    [Fact]
    public async Task Fault_before_commit_discards_the_partial_folder_and_leaves_root_clean()
    {
        // B2-3: the shared `catch when (!committed)` cleanup on a FAULT (not just the tested cancel
        // path). The engine throws on the first TranscribeAsync -> the worker faults -> the run
        // unwinds before the commit, so the partial version folder is deleted, session.json never
        // gains an entry, ActiveVersion stays v1, and the immutable root transcript is untouched.
        string id = await SeedFinalizedAsync();
        byte[] rootJsonl = await File.ReadAllBytesAsync(_paths.TranscriptJsonl(id));
        var throwing = new FakeEngineFactory(plan => new FakeTranscriptionEngine(
            plan.ModelName, _ => throw new InvalidOperationException("transcribe boom")));
        var runner = MakeRunner(engine: throwing);
        var completed = new List<string>();
        runner.RetranscriptionCompleted += cid => { lock (completed) completed.Add(cid); };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(Request(id), CancellationToken.None));

        string expectedVid = TranscriptVersions.NewId(2, "base.en",
            DateOnly.FromDateTime(_time.GetLocalNow().Date));
        Assert.False(Directory.Exists(_paths.VersionDir(id, expectedVid)));   // partial folder discarded
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);
        Assert.Empty(session.Versions);
        Assert.Null(runner.RunningSessionId);
        Assert.Equal(new[] { id }, completed.ToArray());                     // Completed still fires once
        Assert.Equal(rootJsonl, await File.ReadAllBytesAsync(_paths.TranscriptJsonl(id)));   // root untouched
    }

    [Fact]
    public async Task Applies_current_vocabulary_as_prompt_bias_and_records_it()
    {
        string id = await SeedFinalizedAsync();
        var engine = new FakeEngineFactory();
        var runner = MakeRunner(
            settings: new Settings { Vocabulary = new Vocabulary { Terms = ["LocalScribe", "Webex"] } },
            engine: engine);

        string? vid = await runner.RunAsync(Request(id), CancellationToken.None);

        Assert.NotNull(vid);
        Assert.Contains("LocalScribe", engine.LastInitialPrompt);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.True(Assert.Single(session!.Versions).VocabularyApplied);
    }

    [Fact]
    public async Task Commit_runs_under_the_injected_gate_delegate()
    {
        // F2 (whole-branch review): the session.json commit (read-append-flip) must run through
        // the injected runUnderGate seam - CompositionRoot.Build wires this to
        // MaintenanceService.RunForSessionAsync so the runner's commit shares the same per-session
        // lock as the App-side session.json writers (SetActiveVersionAsync, the diarisation
        // Diarised flip, ...) and can never interleave with them. Deterministic: no real threads
        // racing - a fake gate records entry/exit ordering and proves the commit's SaveAsync only
        // ever runs INSIDE an open gate hold, plus that the gate really was invoked for this
        // session id.
        string id = await SeedFinalizedAsync();
        var gateLog = new List<string>();
        int concurrentGateHolders = 0;
        Func<string, Func<CancellationToken, Task>, Task> runUnderGate = async (sid, work) =>
        {
            gateLog.Add($"enter:{sid}");
            // Proves single-flight: if a second commit could run concurrently under a real
            // per-session gate this would trip - the fake enforces it directly since a real
            // SemaphoreSlim(1,1) is exactly MaintenanceService.RunForSessionAsync's shape.
            Assert.Equal(1, Interlocked.Increment(ref concurrentGateHolders));
            try { await work(CancellationToken.None); }
            finally
            {
                Interlocked.Decrement(ref concurrentGateHolders);
                gateLog.Add($"exit:{sid}");
            }
        };
        var runner = MakeRunner(runUnderGate: runUnderGate);

        string? vid = await runner.RunAsync(Request(id), CancellationToken.None);

        Assert.NotNull(vid);
        Assert.Equal(new[] { $"enter:{id}", $"exit:{id}" }, gateLog.ToArray());
        // The gate's work delegate really executed the commit (not just recorded) - session.json
        // reflects it, proving the runner's write happened INSIDE the gate hold, not around it.
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(vid, session!.ActiveVersion);
        Assert.Equal(vid, Assert.Single(session.Versions).Id);
    }

    [Fact]
    public async Task Default_ctor_runs_the_commit_inline_when_no_gate_is_injected()
    {
        // Back-compat: existing Core tests (and this whole file) construct the runner with no
        // runUnderGate - the commit must still land exactly as before (run-inline default).
        string id = await SeedFinalizedAsync();
        var runner = MakeRunner();   // no runUnderGate

        string? vid = await runner.RunAsync(Request(id), CancellationToken.None);

        Assert.NotNull(vid);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(vid, session!.ActiveVersion);
    }
}

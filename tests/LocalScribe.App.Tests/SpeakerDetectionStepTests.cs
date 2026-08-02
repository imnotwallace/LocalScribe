using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The post-import speaker-detection phase (design 2026-07-28 section 3). Runs AFTER
/// AudioImporter.ImportAsync has returned, so a diariser failure can never reach the
/// Directory.Delete-the-whole-session catch at AudioImporter.cs:205-210, and the Diarised flag it
/// commits is not clobbered by the Save-stage snapshot window at AudioImporter.cs:183-200.</summary>
public sealed class SpeakerDetectionStepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_sds_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeEngine : IDiarisationEngine
    {
        public int Calls { get; private set; }
        public int? LastForced { get; private set; }
        public bool? LastEmitEmbeddings { get; private set; }
        public string? LastFlacPath { get; private set; }
        public Exception? Throw { get; set; }
        public DiarisationResult Next { get; set; } = new(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "sherpa");

        public Task<DiarisationResult> DiariseAsync(
            DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            Calls++;
            LastForced = r.ForcedClusterCount;
            LastEmitEmbeddings = r.EmitEmbeddings;
            LastFlacPath = r.FlacPath;
            if (Throw is not null) throw Throw;
            p.Report(0.5);
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>An imported, finalized, NOT-yet-diarised session with one retained Local leg and
    /// two Local segments - exactly the shape AudioImporter leaves behind for a mono import
    /// (SessionMeta.LocalCount stays at its default 1; imports never raise it).
    /// <paramref name="helperExePresent"/> (fix round 1, Finding 1): false leaves both sherpa
    /// models on disk but skips writing LocalScribe.Diarizer.exe, so DiarisationAvailability.Probe
    /// fails on the exe specifically - the Unavailable branch, otherwise never exercised.
    /// <paramref name="initialMarkerCount"/> (fix round 1, Finding 2): the STARTING session.json
    /// MarkerCount, seeded independently of the transcript's actual (zero) marker count so a test
    /// can tell a true recount from a naive increment.</paramref></summary>
    private (SpeakerDetectionStep step, StoragePaths paths, string id, FakeEngine engine)
        MakeImportedSession(bool retainAudio = true, string audioRetention = "keep",
            bool helperExePresent = true, int initialMarkerCount = 0)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            Origin = "imported",
            RetainedAudioSources = retainAudio ? [SourceKind.Local] : [],
            MarkerCount = initialMarkerCount,
        }, default).GetAwaiter().GetResult();

        new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta(), default).GetAwaiter().GetResult();

        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();

        if (retainAudio)
            File.WriteAllBytes(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), [1, 2, 3]);

        // Both sherpa models + the helper exe present, so the availability gate passes.
        string models = Path.Combine(_root, "models");
        foreach (var name in new[] { DiarisationModels.Segmentation, DiarisationModels.Embedding })
        {
            string p = Path.Combine(models, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, [1, 2, 3]);
        }
        string exe = Path.Combine(_root, "LocalScribe.Diarizer.exe");
        if (helperExePresent) File.WriteAllBytes(exe, [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings { AudioRetention = audioRetention });
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        var engine = new FakeEngine();
        var step = new SpeakerDetectionStep(engine, maintenance, paths, settings,
            name => Path.Combine(models, name.Replace('/', Path.DirectorySeparatorChar)), exe,
            TimeProvider.System);
        return (step, paths, id, engine);
    }

    private static async Task<IReadOnlyList<string>> MarkerTextsAsync(StoragePaths paths, string id)
    {
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
        return lines.Where(l => l.Kind == TranscriptKind.Marker).Select(l => l.Text).ToList();
    }

    [Fact]
    public async Task Auto_commits_default_labels_and_flips_diarised()
    {
        var (step, paths, id, engine) = MakeImportedSession();

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Committed, outcome.Result);
        Assert.Equal(2, outcome.ClusterCount);
        Assert.Null(engine.LastForced);                 // auto == ForcedClusterCount null
        Assert.True(engine.LastEmitEmbeddings);         // so the voiceprint chips work on reopen

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Local Speaker 1", speakers!.Names["Local:0"]);
        Assert.Equal("Local Speaker 2", speakers.Names["Local:1"]);
        Assert.Equal("Local:0", speakers.Assignments["Local"]["0"]);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.True(session!.Diarised);

        // Success leaves no marker: speakers.json + Diarised ARE the record.
        Assert.Empty(await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task Declared_forces_the_count_and_writes_it_to_meta()
    {
        var (step, paths, id, engine) = MakeImportedSession();

        await step.RunAsync(id, SpeakerDetection.Declared, 3, null, default);

        Assert.Equal(3, engine.LastForced);
        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(3, meta!.LocalCount);
    }

    [Fact]
    public async Task Auto_writes_the_committed_cluster_count_to_meta()
    {
        var (step, paths, id, _) = MakeImportedSession();

        await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(2, meta!.LocalCount);   // imports leave this at 1; detection makes it truthful
        Assert.False(meta.Edited);           // never flip the manual-correction flag
    }

    [Fact]
    public async Task One_voice_commits_nothing_and_markers()
    {
        // A collapse to one cluster is the expected outcome for genuinely one-voice audio: the
        // in-house silhouette scan in SpeakerClustering (Core) falls back to a single cluster when
        // no candidate split clears its floor. Labelling a whole call "Local Speaker 1" is not an
        // improvement over "Me", and since SaveDiarisationAsync never runs, Diarised stays false -
        // so without this marker nothing would record that detection happened at all.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Next = new DiarisationResult([new DiarisedSegment(0, 2000, 0)], 1, "sherpa");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.OneVoice, outcome.Result);
        Assert.Null(await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default));
        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.False(session!.Diarised);
        Assert.Contains(Markers.SpeakerDetectionOneVoice, await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task A_thrown_engine_leaves_the_session_intact_and_markers()
    {
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new DiarisationException(DiarisationErrorCode.HelperCrash, "boom");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Failed, outcome.Result);
        // The import itself is untouched: folder, transcript segments and audio all still there.
        Assert.True(Directory.Exists(paths.SessionDir(id)));
        Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Segment));
        Assert.Contains(await MarkerTextsAsync(paths, id),
            t => t.StartsWith("speaker detection did not complete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_helper_exe_is_caught_even_though_it_throws_Win32Exception()
    {
        // Process.Start throws Win32Exception out of ProcessDiarisationHelper.cs:33 and
        // SherpaHelperDiariser.cs:47 does not catch it, so it propagates RAW - not as a
        // DiarisationException. Catching only DiarisationException would let it escape.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified.");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Failed, outcome.Result);
        Assert.True(Directory.Exists(paths.SessionDir(id)));
    }

    [Fact]
    public async Task Cancellation_keeps_the_import_and_writes_no_marker()
    {
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new OperationCanceledException();

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Cancelled, outcome.Result);
        Assert.True(Directory.Exists(paths.SessionDir(id)));
        // Cancelling is a choice, not a degradation - nothing to record.
        Assert.Empty(await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task No_retained_leg_reports_NoAudio_without_calling_the_engine()
    {
        var (step, paths, id, engine) = MakeImportedSession(retainAudio: false, audioRetention: "never");

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.NoAudio, outcome.Result);
        Assert.Equal(0, engine.Calls);
        Assert.Contains(Markers.SpeakerDetectionNoAudio, await MarkerTextsAsync(paths, id));
    }

    [Fact]
    public async Task Unavailable_reports_without_calling_the_engine_when_the_helper_exe_is_missing()
    {
        // Fix round 1, Finding 1: every other fixture writes both sherpa models AND the helper
        // exe, so DiarisationAvailability.Probe always returned null and the entire Unavailable
        // branch - the runtime re-check, its marker write, its WriteDeclaredCountAsync call, its
        // outcome value - never ran in any test; a regression there would still pass the whole
        // suite. Both models stay present here so the exe is unambiguously the missing thing.
        var (step, paths, id, engine) = MakeImportedSession(helperExePresent: false);

        var outcome = await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(SpeakerDetectionResult.Unavailable, outcome.Result);
        Assert.Equal(0, engine.Calls);                 // must never reach the engine
        Assert.Contains(await MarkerTextsAsync(paths, id),
            t => t.StartsWith("speaker detection did not complete", StringComparison.Ordinal));
        // The import itself is untouched: folder and transcript segments both still there.
        Assert.True(Directory.Exists(paths.SessionDir(id)));
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Segment));
    }

    [Fact]
    public async Task Unavailable_still_writes_the_declared_count_to_meta()
    {
        // Fix round 1, Finding 1: the Unavailable branch pre-configures the force-N retry button
        // exactly like every other failure path - unproven until this test existed.
        //
        // The outcome/engine-calls assertions below are not decorative: Declared(n) is ALSO
        // written on the Committed path (this task's own Declared/Committed fix earlier in this
        // round), so "meta.LocalCount == 5" alone cannot tell "the Unavailable branch actually
        // ran" from "some other branch also honoured the declared count coincidentally" - a
        // mutation-test check caught exactly that gap in an earlier draft of this test.
        var (step, paths, id, engine) = MakeImportedSession(helperExePresent: false);

        var outcome = await step.RunAsync(id, SpeakerDetection.Declared, 5, null, default);

        Assert.Equal(SpeakerDetectionResult.Unavailable, outcome.Result);
        Assert.Equal(0, engine.Calls);
        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(5, meta!.LocalCount);
    }

    [Fact]
    public async Task Every_marker_it_writes_corrects_MarkerCount()
    {
        // AudioImporter.cs:185-200 recounts markers into session.json during the Save stage;
        // anything appended AFTER that is not counted. Detection runs after Save.
        //
        // Fix round 1, Finding 2: MarkerCount is seeded DELIBERATELY WRONG (7) while the
        // transcript actually holds zero markers, so a true recount and a naive increment give
        // DIFFERENT answers and this test can tell them apart. A real recount reads the
        // transcript after the one marker this run writes and lands on 1; MarkerCount + 1 would
        // land on 8. Seeding 0 here (matching the transcript's truth) would make both
        // implementations produce the same "1" and prove nothing - do not "simplify" it back.
        var (step, paths, id, engine) = MakeImportedSession(initialMarkerCount: 7);
        engine.Next = new DiarisationResult([new DiarisedSegment(0, 2000, 0)], 1, "sherpa");

        await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(1, session!.MarkerCount);
    }

    [Fact]
    public async Task Declared_still_writes_the_count_when_detection_fails()
    {
        // The user asserted it, and it pre-configures the force-N button for a manual retry.
        var (step, paths, id, engine) = MakeImportedSession();
        engine.Throw = new DiarisationException(DiarisationErrorCode.HelperCrash, "boom");

        await step.RunAsync(id, SpeakerDetection.Declared, 4, null, default);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(4, meta!.LocalCount);
    }

    [Fact]
    public async Task Reports_determinate_progress_from_the_helper()
    {
        var (step, paths, id, _) = MakeImportedSession();
        var seen = new List<double>();

        await step.RunAsync(id, SpeakerDetection.Auto, null,
            new SynchronousProgress<double>(seen.Add), default);

        Assert.NotEmpty(seen);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public async Task Points_the_engine_at_the_retained_leg()
    {
        var (step, paths, id, engine) = MakeImportedSession();

        await step.RunAsync(id, SpeakerDetection.Auto, null, null, default);

        Assert.Equal(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), engine.LastFlacPath);
    }

    [Fact]
    public async Task Off_is_a_programming_error()
    {
        var (step, _, id, _) = MakeImportedSession();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            step.RunAsync(id, SpeakerDetection.Off, null, null, default));
    }
}

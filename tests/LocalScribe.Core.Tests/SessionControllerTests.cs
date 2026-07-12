using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vocabulary;
using NAudio.Wave;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-live-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Start_then_stop_produces_finalized_session_folder()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        var states = new List<SessionState>();
        c.StateChanged += s => states.Add(s);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.Equal(id, c.CurrentSessionId);

        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None);
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Null(c.CurrentSessionId);
        Assert.Equal([SessionState.Recording, SessionState.Finalizing, SessionState.Idle], states);

        await c.PendingFinalize;                            // transcription tail + session.json/projections land in the background
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
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root);
        var lines = new List<TranscriptLine>();
        c.LineInserted += (_, l) => { lock (lines) lines.Add(l); };

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // LineInserted + transcript.jsonl fill from the background drain

        Assert.Equal(2, lines.Count(l => l.Kind == TranscriptKind.Segment));
        Assert.Contains(lines, l => l.Source == TranscriptSource.Local && l.SpeakerLabel == "Me");
        Assert.Contains(lines, l => l.Source == TranscriptSource.Remote && l.SpeakerLabel == "Them");
        var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Equal(2, stored.Count(l => l.Kind == TranscriptKind.Segment));
    }

    [Fact]
    public async Task Pinned_mic_unavailable_emits_the_fallback_marker()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        provider.MicSnapshot = new MicSnapshot
        { Mode = MicMode.FollowDefault, Name = "Default Mic", FellBackToDefault = true };

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // marker reaches transcript.jsonl via the background drain

        var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(stored, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.PinnedMicUnavailable);
    }

    [Fact]
    public async Task Follow_default_mic_emits_no_fallback_marker()
    {
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root);   // FakeProvider default: FellBackToDefault=false

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // transcript.jsonl fully written by the background drain

        var stored = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.DoesNotContain(stored, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.PinnedMicUnavailable);
    }

    [Fact]
    public async Task Retention_never_skips_audio_files()
    {
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root, new Settings { AudioRetention = "never" });
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // session.json written by the background finalize

        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Empty(record!.RetainedAudioSources);
    }

    [Fact]
    public async Task Second_start_is_ignored_with_notice()
    {
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root);
        string? notice = null;
        c.Notice += n => notice = n;

        string? first = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        string? second = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);                                 // single-session guard (design 5)
        Assert.NotNull(notice);
        Assert.Equal(1, provider.MicCreates);
        await c.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_when_idle_is_ignored_with_notice()
    {
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root);
        string? notice = null;
        c.Notice += n => notice = n;
        Assert.Null(await c.StopAsync(CancellationToken.None));
        Assert.NotNull(notice);
        Assert.Equal(SessionState.Idle, c.State);
    }

    [Fact]
    public async Task Stop_swallows_worker_fault_and_finalizes_cleanly()
    {
        // Engine creation hard-faults (e.g. missing ggml model). RunAsync captures it in the
        // worker task; the C1 guard then cancels the feed legs. Fix #3 / Task 6: this is now a
        // TranscriptionFailed fault, not a fatal one - Stop must NOT rethrow it (audio survived
        // via the capture/feed token split, Task 5); the session finalizes normally instead.
        // See SessionControllerTranscriptionFaultTests for the marker/audio-retained assertions.
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: new FakeEngineFactory(
            (BackendPlan _) => throw new InvalidDataException("model missing")));

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);                                  // the fault is async - Start succeeds

        string? stopped = await c.StopAsync(CancellationToken.None);   // must NOT throw
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Null(c.CurrentSessionId);
        await c.PendingFinalize;                            // worker fault is now swallowed in the background finalize
    }

    [Fact]
    public async Task Stop_with_faulting_leg_still_settles_sibling_and_surfaces_fault()
    {
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root);
        provider.ThrowOnLocalStop = true;                    // genuine (non-OCE) local leg fault

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);

        // The faulting leg's own source is not disposed on a Stop() throw - a
        // LiveSourcePipeline edge accepted as-is (real WASAPI Stop does not throw).
        var ex = await Assert.ThrowsAsync<IOException>(() => c.StopAsync(CancellationToken.None));
        Assert.Equal("stop failed", ex.Message);
        Assert.True(provider.LastRemote!.Disposed);          // sibling leg settled anyway
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Null(c.CurrentSessionId);
    }

    [Fact]
    public async Task Failed_start_disposes_created_sources_and_stays_idle()
    {
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root);
        provider.ThrowOnNextRemoteCreate = true;             // mic created first, then remote throws

        await Assert.ThrowsAsync<InvalidOperationException>(() => c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None));
        Assert.True(provider.LastMic!.Disposed);             // no orphaned live mic capture
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Null(c.CurrentSessionId);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);   // throw was one-shot
        Assert.NotNull(id);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.Equal(id, await c.StopAsync(CancellationToken.None));
        Assert.Equal(SessionState.Idle, c.State);
    }

    [Fact]
    public async Task Manual_start_derives_app_from_per_process_plan()
    {
        // FakeProvider's default RemoteSnapshot is PerProcess on "CiscoCollabHost"
        // (LiveTestDoubles.cs:97-98) - exactly a resolved Webex plan.
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root);
        var options = LiveTestDoubles.Options() with { App = AppKind.Manual };

        string? id = await c.StartAsync(options, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.Equal(AppKind.Manual, options.App);          // caller's options stay Manual
        Assert.Contains("_Webex_", id!);                    // folder id embeds the derived app
        await c.PendingFinalize;                            // session.json written by the background finalize
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Webex, record!.App);           // session.json App derived
        var meta = await new MetadataStore(paths.MetaJson(id!)).LoadAsync(CancellationToken.None);
        Assert.Equal(Medium.Webex, meta!.Medium);           // CreateDefault maps AppKind -> Medium
        Assert.StartsWith("Webex", meta.Title);             // default title derived too
    }

    [Fact]
    public async Task Manual_start_with_explicit_system_mix_stays_manual()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        // User-pinned systemMix: FellBackToSystemMix=false and App is the raw setting
        // passthrough (RemoteCapturePlanner.cs:27-28), NOT a matched image - never derive.
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "Webex", FellBackToSystemMix = false };

        string? id = await c.StartAsync(
            LiveTestDoubles.Options() with { App = AppKind.Manual }, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // session.json written by the background finalize

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Manual, record!.App);
    }

    [Fact]
    public async Task Manual_start_derives_from_full_mix_fallback_that_exposes_the_matched_image()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        // Planner matched chrome but forced full mix (shared-audio image): the fallback still
        // exposes the matched image through RemotePlan.App -> RemoteSnapshot.App.
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "chrome", FellBackToSystemMix = true };

        string? id = await c.StartAsync(
            LiveTestDoubles.Options() with { App = AppKind.Manual }, CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // session.json written by the background finalize

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Browser, record!.App);
    }

    [Fact]
    public async Task Non_manual_start_never_rederives()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        provider.RemoteSnapshot = new RemoteSnapshot
        { Mode = RemoteMode.SystemMix, App = "chrome", FellBackToSystemMix = true };

        // Options() defaults App to Webex - an explicit user choice must be honored verbatim.
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // session.json written by the background finalize

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(AppKind.Webex, record!.App);
    }

    [Fact]
    public async Task StartAsync_reads_the_current_settings_not_a_construction_snapshot()
    {
        // Design 6.2 seam: AudioRetention flips to "never" AFTER construction but BEFORE Start.
        // The session must create no audio writers - same observable effect the existing
        // Retention_never_skips_audio_files test pins for a construction-time "never".
        var paths = new StoragePaths(_root);
        var provider = new FakeProvider();
        var clock = new FakeClock();
        Settings current = new();                            // retention "keep" at construction
        var c = new SessionController(paths, () => current, new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            provider, () => clock,
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero)), "0.4.0",
            availableModels: () => new HashSet<string>(new[] { "base.en", "tiny.en" }, StringComparer.Ordinal));

        current = new Settings { AudioRetention = "never" }; // the swap SettingsService.SaveAsync performs

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // session.json written by the background finalize

        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac)));
        Assert.False(File.Exists(paths.AudioFile(id!, SourceKind.Remote, AudioFormat.Flac)));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Empty(record!.RetainedAudioSources);
    }

    [Fact]
    public async Task Stop_pads_retained_audio_to_the_session_clock()
    {
        // Stage 5.4 Phase 3 write-side fix: the fake legs deliver 7 x 512 = 3584 samples
        // (~224 ms) per side, but the session clock reads 5000 ms at Stop. Finalize must pad
        // each retained file with silence to exactly the session clock so the audio and the
        // recorded DurationMs agree. Wav (not the Flac default) so the file is assertable
        // with WaveFileReader: 16 kHz mono 16-bit => 16 samples per ms, BlockAlign 2.
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(
            _root, new Settings { AudioFormat = AudioFormat.Wav });

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);

        clock.ElapsedMs = 5000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;                            // session.json written by the background finalize (audio was padded synchronously)

        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            using var r = new WaveFileReader(paths.AudioFile(id!, kind, AudioFormat.Wav));
            Assert.Equal(80000, r.Length / r.WaveFormat.BlockAlign);   // 5000 ms * 16 samples/ms
        }

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(5000, record!.DurationMs);            // clock and audio agree exactly
    }

    [Fact]
    public async Task Faulted_stop_never_pads_retained_audio()
    {
        // Pins the `if (!faulted)` guard in StopAsync's teardown finally: a faulting leg must
        // never let PadToMs fabricate a silent tail. Same fault mechanism as
        // Stop_with_faulting_leg_still_settles_sibling_and_surfaces_fault (provider.ThrowOnLocalStop),
        // same Wav-format harness as Stop_pads_retained_audio_to_the_session_clock. The clock is
        // advanced to 5000 ms BEFORE the faulting Stop so a pad WOULD stretch both files to
        // 80000 samples (5000 ms * 16 samples/ms) if the guard were deleted.
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(
            _root, new Settings { AudioFormat = AudioFormat.Wav });
        provider.ThrowOnLocalStop = true;

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);

        clock.ElapsedMs = 5000;
        var ex = await Assert.ThrowsAsync<IOException>(() => c.StopAsync(CancellationToken.None));
        Assert.Equal("stop failed", ex.Message);
        Assert.Equal(SessionState.Idle, c.State);

        // Both legs land at the SAME unpadded count, even though only the local leg's Stop()
        // throws: FakeCaptureSource replays all its frames synchronously inside Start() (well
        // before StopAsync ever runs), so the background feed task has already drained the
        // whole buffer into the AlignedAudioWriter by the time Stop() is called - the fault
        // only prevents the leg's own EOF flush/teardown, not audio already written. 7 frames
        // (4 speech + 3 silence) x 512 samples/frame = 3584 samples per side (empirically
        // confirmed stable across 20+ repeated runs). The essential property under test is not
        // this specific number but that it is EXACTLY what capture recorded and STRICTLY less
        // than the 80000-sample pad target - no fabricated tail on a faulted finalize.
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            using var r = new WaveFileReader(paths.AudioFile(id!, kind, AudioFormat.Wav));
            long samples = r.Length / r.WaveFormat.BlockAlign;
            Assert.Equal(3584, samples);
            Assert.True(samples < 80000, $"{kind} file must not be padded to the session clock on a faulted finalize.");
        }
    }

    [Fact]
    public async Task Start_biases_prompt_with_picked_matter_terms()
    {
        string root = Path.Combine(Path.GetTempPath(), "ls-ctrl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new StoragePaths(root);
            await new MatterStore(paths.MattersDir).SaveAsync(new Matter
            {
                Id = "M-2026-014", Name = "Doe v. State",
                Vocabulary = new Model.Vocabulary { Terms = new[] { "arraignment", "voir dire" } },
            }, default);

            var factory = new FakeEngineFactory();
            var (controller, _, _, _) = LiveTestDoubles.MakeController(root, engineFactory: factory);
            string? id = await controller.StartAsync(
                LiveTestDoubles.Options() with { MatterIds = new[] { "M-2026-014" } }, default);
            await controller.StopAsync(default);
            await controller.PendingFinalize;              // drain the background finalize before the dir is deleted

            Assert.NotNull(id);
            Assert.Contains("arraignment", factory.LastInitialPrompt);
            Assert.Contains("voir dire", factory.LastInitialPrompt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Start_with_no_matters_builds_a_global_only_prompt()
    {
        string root = Path.Combine(Path.GetTempPath(), "ls-ctrl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings
            { Vocabulary = new Model.Vocabulary { Terms = new[] { "globalword" } } };
            var factory = new FakeEngineFactory();
            var (controller, _, _, _) = LiveTestDoubles.MakeController(root, settings, factory);
            await controller.StartAsync(LiveTestDoubles.Options(), default);   // no MatterIds
            await controller.StopAsync(default);
            await controller.PendingFinalize;              // drain the background finalize before the dir is deleted

            Assert.Equal("globalword", factory.LastInitialPrompt);            // global only, no matter terms
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Start_tolerates_a_missing_matter_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "ls-ctrl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var factory = new FakeEngineFactory();
            var (controller, _, _, _) = LiveTestDoubles.MakeController(root, engineFactory: factory);
            // M-GHOST was never written to disk - Start must not throw, just skip it.
            string? id = await controller.StartAsync(
                LiveTestDoubles.Options() with { MatterIds = new[] { "M-GHOST" } }, default);
            await controller.StopAsync(default);
            await controller.PendingFinalize;              // drain the background finalize before the dir is deleted

            Assert.NotNull(id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionFinalizeCompleted_fires_once_and_FinalizingSessionId_tracks_the_inflight_id()
    {
        // GatedEngineFactory holds the worker build closed, so FinalizeInBackgroundAsync parks at
        // `await s.WorkerLoop` after Stop returns Idle - the window in which _finalizing is set.
        var gated = new GatedEngineFactory();
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        Assert.Null(c.FinalizingSessionId);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Null(c.FinalizingSessionId);                       // recording, not finalizing

        var completed = new List<string>();
        c.SessionFinalizeCompleted += cid => { lock (completed) completed.Add(cid); };

        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Equal(id, c.FinalizingSessionId);                  // finalize in flight (gated)
        Assert.False(c.PendingFinalize.IsCompleted);

        gated.CreateGate.Set();                                   // let the finalize drain + persist
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { id }, completed.ToArray());          // fired exactly once, with the id
        Assert.Null(c.FinalizingSessionId);                       // cleared after completion
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);                       // clean finalize wrote EndedAtUtc
    }

    [Fact]
    public async Task SessionFinalizeCompleted_fires_once_on_a_failed_finalize()
    {
        // Force PersistFinalAsync/the writer drain to fail: make transcript.jsonl a DIRECTORY (the
        // same fault mechanism MaintenanceServiceTests uses) while the finalize is gated, so the
        // background drain throws and FinalizeInBackgroundAsync takes its FINALIZE_FAILED catch - the
        // event must STILL fire once from the finally, and EndedAtUtc is never written.
        var gated = new GatedEngineFactory();
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var errors = new List<string>();
        c.ErrorRaised += e => { lock (errors) errors.Add(e); };
        var completed = new List<string>();
        c.SessionFinalizeCompleted += cid => { lock (completed) completed.Add(cid); };

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(id, c.FinalizingSessionId);

        Directory.CreateDirectory(paths.TranscriptJsonl(id!));    // a dir where a file must be -> writes throw
        gated.CreateGate.Set();
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));   // never throws to the awaiter

        Assert.Contains("FINALIZE_FAILED", errors);
        Assert.Equal(new[] { id }, completed.ToArray());          // fires once even on the failure path
        Assert.Null(c.FinalizingSessionId);
    }
}

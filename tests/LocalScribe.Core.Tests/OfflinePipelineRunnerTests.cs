using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

public class OfflinePipelineRunnerTests
{
    /// <summary>Energy-threshold probe: loud window = speech. Deterministic, no ONNX.</summary>
    private sealed class EnergyProbe : ISpeechProbabilityModel
    {
        public float SpeechProbability(ReadOnlySpan<float> window)
            => SegmentAudio.RmsDb(window) > -30.0 ? 0.95f : 0.02f;
        public void Reset() { }
    }

    private sealed class EchoFactory : IEngineFactory
    {
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
            => Task.FromResult<ITranscriptionEngine>(new FakeTranscriptionEngine(plan.ModelName,
                s => new TranscriptionResult($"[{s.Source} {s.StartMs}-{s.EndMs}]", "en", 0.01)));
    }

    /// <summary>Mirrors WhisperEngineFactory.CreateAsync on a missing ggml model: throws
    /// SYNCHRONOUSLY (not a faulted Task) - same as ModelPaths.Require (finding C1).</summary>
    private sealed class FaultingFactory : IEngineFactory
    {
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
            => throw new FileNotFoundException($"Model file missing: ggml-{plan.ModelName}.bin");
    }

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

    [Fact]
    public async Task Wav_pair_becomes_a_complete_finalized_session_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string localWav = Path.Combine(root, "in-local.wav");
            string remoteWav = Path.Combine(root, "in-remote.wav");
            WriteBurstWav(localWav, (200, 1500));                  // one Local utterance
            WriteBurstWav(remoteWav, (2500, 1500));                // one later Remote utterance

            var paths = new StoragePaths(Path.Combine(root, "store"));
            // Wav: no FLAC dependency here. Language fixed to "en": with only 2 segments the
            // auto probe (3 utterances) never locks and session.language would stay "auto".
            var settings = new Settings { AudioFormat = AudioFormat.Wav, Language = "en" };
            var time = new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 32, 5, TimeSpan.Zero));
            var runner = new OfflinePipelineRunner(paths, settings, new EchoFactory(),
                () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
                new FakeClock(), time, appVersion: "0.2.0-test");

            string id = await runner.RunAsync(new OfflineRunOptions
            { LocalWavPath = localWav, RemoteWavPath = remoteWav }, default);

            // JSONL: one segment per burst, Local finalized first (fed first)
            var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
            Assert.Equal(2, lines.Count);
            Assert.Equal(TranscriptSource.Local, lines[0].Source);
            Assert.Equal("Me", lines[0].SpeakerLabel);
            Assert.Equal(TranscriptSource.Remote, lines[1].Source);
            Assert.NotNull(lines[0].RmsDb);

            // session.json finalized with counts, duration, recorded actuals, timezone
            var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
            Assert.NotNull(session!.EndedAtUtc);
            Assert.Equal(2, session.SegmentCount);
            Assert.Equal(0, session.MarkerCount);
            Assert.True(session.DurationMs >= 3500);
            Assert.Equal("CPU", session.Backend);
            Assert.Equal("en", session.Language);
            Assert.NotNull(session.TimeZoneId);
            Assert.Equal(AppKind.Manual, session.App);

            // projections + retained audio on disk (spec 9 self-contained folder)
            Assert.True(File.Exists(paths.TranscriptMd(id)));
            Assert.True(File.Exists(paths.TranscriptTxt(id)));
            Assert.True(File.Exists(paths.SessionTxt(id)));
            Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav)));
            Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Wav)));
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, session.RetainedAudioSources);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Retention_never_skips_audio_files()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string localWav = Path.Combine(root, "in-local.wav");
            WriteBurstWav(localWav, (200, 1000));
            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { AudioFormat = AudioFormat.Wav, AudioRetention = "never" };
            var runner = new OfflinePipelineRunner(paths, settings, new EchoFactory(),
                () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
                new FakeClock(), new ManualUtcTimeProvider(DateTimeOffset.UnixEpoch), "0.2.0-test");

            string id = await runner.RunAsync(new OfflineRunOptions { LocalWavPath = localWav }, default);

            Assert.False(File.Exists(paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav)));
            var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
            Assert.Empty(session!.RetainedAudioSources);
            Assert.Single(await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Regression for finding C1: when the worker faults (e.g. missing ggml model),
    /// the feeding loop must abort promptly instead of hanging forever in EnqueueAsync against
    /// a bounded, reader-less queue - and RunAsync must fault with the ORIGINAL exception, not
    /// an OperationCanceledException mask. QueueCapacity is shrunk to 1 and the WAV carries 4
    /// well-separated bursts (> capacity) so, pre-fix, the 2nd EnqueueAsync call would block
    /// forever with no reader left to drain the queue.</summary>
    [Fact]
    public async Task Worker_fault_aborts_feeding_promptly_and_propagates_the_original_exception()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string localWav = Path.Combine(root, "in-local.wav");
            WriteBurstWav(localWav, (200, 800), (500, 800), (500, 800), (500, 800));

            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { AudioFormat = AudioFormat.Wav, Language = "en" };
            var options = new OfflineRunOptions
            {
                LocalWavPath = localWav,
                Worker = new TranscriptionWorkerOptions { QueueCapacity = 1 },
            };
            var runner = new OfflinePipelineRunner(paths, settings, new FaultingFactory(),
                () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
                new FakeClock(), new ManualUtcTimeProvider(DateTimeOffset.UnixEpoch), "0.2.0-test");

            var run = runner.RunAsync(options, default);
            var completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

            Assert.Same(run, completed);       // else: hung - regression of finding C1
            var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => run);
            Assert.Contains("ggml", ex.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}

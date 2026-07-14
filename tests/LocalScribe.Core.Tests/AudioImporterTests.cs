using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

public sealed class AudioImporterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-importer-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public AudioImporterTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new StoragePaths(Path.Combine(_root, "store"));
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // --- per-file helpers (OfflinePipelineRunnerTests convention: small private copies) ---

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

    /// <summary>Deterministic machine zone (+10:00, no DST) so recorded-date identity asserts are
    /// machine-independent - AudioImporter only reads LocalTimeZone from this provider.</summary>
    private sealed class FixedZoneTime : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "import-test-zone", TimeSpan.FromHours(10), "import-test-zone", "import-test-zone");
    }

    private sealed class FakeDecoder : IAudioDecoder
    {
        public AudioProbeResult Probe { get; set; } = new();
        public string? DecodedWavPath { get; set; }
        public Func<CancellationToken, Task>? BeforeDecode { get; set; }
        public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(Probe);
        public async Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
        {
            if (BeforeDecode is not null) await BeforeDecode(ct);
            using var r = new WaveFileReader(DecodedWavPath!);
            return new DecodedAudio
            {
                PcmWavPath = DecodedWavPath!,
                SampleRate = r.WaveFormat.SampleRate,
                Channels = r.WaveFormat.Channels,
                DurationMs = (long)r.TotalTime.TotalMilliseconds,
            };
        }
    }

    // 200 ms silence + 1500 ms tone + 1000 ms silence (2700 ms total): EnergyProbe segments the
    // burst; the trailing silence closes it (the WriteBurstWav idiom, widened to N channels).
    private string WriteBurstWav(string name, int rate, int channels, params int[] toneChannels)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, channels));
        int silence = rate / 5, speech = rate * 3 / 2, tail = rate;
        var buf = new float[(silence + speech + tail) * channels];
        for (int f = 0; f < speech; f++)
            foreach (int ch in toneChannels)
                buf[(silence + f) * channels + ch] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    private AudioImporter MakeImporter(FakeDecoder decoder, Settings? settings = null)
        => new(_paths, settings ?? new Settings { Language = "en" }, decoder, new EchoFactory(),
            () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), new FixedZoneTime(), appVersion: "0.2.0-test");

    private static ImportRequest Request(string sourcePath, string title = "Client call",
        StereoMapping stereo = StereoMapping.Downmix) => new()
    {
        SourcePath = sourcePath, Title = title,
        RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
        MatterIds = ["M-2026-001"], Stereo = stereo,
    };

    [Fact]
    public async Task Import_creates_a_finalized_session_with_provenance_at_the_recorded_date()
    {
        // The "original" is arbitrary bytes with an .mp3 name - the fake decoder never reads it,
        // which proves the importer hashes/copies the ORIGINAL and decodes via the seam.
        string source = Path.Combine(_root, "hearing recording.mp3");
        byte[] originalBytes = new byte[4096];
        Random.Shared.NextBytes(originalBytes);
        await File.WriteAllBytesAsync(source, originalBytes);
        var originalWrite = File.GetLastWriteTimeUtc(source);

        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-mono.wav", 44100, 1, 0),
            Probe = new AudioProbeResult
            {
                FormatName = "mp3", FileSizeBytes = originalBytes.Length,
                ClaimedDurationMs = 2700, ClaimedChannels = 1, ClaimedSampleRate = 44100,
                MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
            },
        };
        var stages = new List<ImportStage>();
        bool confirmCalled = false;

        string id = await MakeImporter(decoder).ImportAsync(Request(source),
            new SynchronousProgress<ImportStage>(stages.Add),
            _ => { confirmCalled = true; return Task.FromResult(true); },
            CancellationToken.None);

        // Identity: the RECORDED date (2026-03-05 14:30 +10:00) drives the id and StartedAtUtc.
        Assert.Equal("2026-03-05_1430_Manual_client-call", id);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero), session!.StartedAtUtc);
        Assert.Equal(600, session.UtcOffsetMinutes);
        // EndedAtUtc round-trips through UtcIso8601Converter, which INTENTIONALLY truncates
        // sub-second precision on write (spec section 1.2 timestamp precision - see
        // UtcIso8601Converter's own doc comment: "endedAtUtc - startedAtUtc may disagree with
        // durationMs by up to 1s. Never rely on fractional seconds in *AtUtc."). StartedAtUtc here
        // is a whole second (a pinned recorded date/time has no fractional component) but the
        // 2700 ms decoded duration is not, so an exact-equality assert against a freshly computed
        // (non-truncated) sum is unsatisfiable by design; assert decoded-truth derivation within
        // that documented 1s budget instead.
        Assert.True(
            (session.StartedAtUtc.AddMilliseconds(session.ImportedSource!.DecodedDurationMs)
                - session.EndedAtUtc!.Value).Duration() < TimeSpan.FromSeconds(1),
            $"started={session.StartedAtUtc:o} decodedMs={session.ImportedSource.DecodedDurationMs} ended={session.EndedAtUtc:o}");

        // Provenance: byte-identical copy, hash over the original bytes, original untouched.
        string copy = _paths.SourceFile(id, "hearing recording.mp3");
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(copy));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(originalBytes)),
            session.ImportedSource.Sha256);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(source));
        Assert.Equal(originalWrite, File.GetLastWriteTimeUtc(copy));   // timestamps mirrored on the copy

        Assert.Equal("imported", session.Origin);
        Assert.Equal("hearing recording.mp3", session.ImportedSource.FileName);
        Assert.Equal("mp3", session.ImportedSource.ContainerFormat);
        Assert.Equal(2700, session.ImportedSource.ClaimedDurationMs);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2600, 2800);
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);
        Assert.Equal(1, session.ImportedSource.DecodedChannels);
        Assert.Equal("mono", session.ImportedSource.ChannelMapping);
        Assert.False(session.ImportedSource.DurationMismatch);
        Assert.False(confirmCalled);                                  // within 1 percent: no gate

        // A NORMAL v1-root session: transcript + FLAC leg + projections + meta.
        Assert.Equal([SourceKind.Local], session.Sources);
        Assert.Equal([SourceKind.Local], session.RetainedAudioSources);
        Assert.True(session.SegmentCount >= 1);
        // Weights provenance (7d6c88d): the runner records the exact ggml file at its finalize
        // and the Save-stage `record with {...}` preserves it - an imported session carries the
        // same WeightsFile evidence as a live one (FakeTranscriptionEngine defaults to
        // "ggml-{model}.bin").
        Assert.Equal($"ggml-{session.Model}.bin", session.WeightsFile);
        Assert.True(File.Exists(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        Assert.True(File.Exists(_paths.TranscriptMd(id)));
        var meta = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Client call", meta!.Title);
        Assert.Equal(["M-2026-001"], meta.MatterIds);

        Assert.Equal([ImportStage.Copy, ImportStage.Decode, ImportStage.Transcribe, ImportStage.Save],
            stages);
    }

    [Fact]
    public async Task Stereo_split_maps_left_to_local_right_to_remote_and_swap_reverses()
    {
        string source = Path.Combine(_root, "call.m4a");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-stereo.wav", 16000, 2, 0),   // tone LEFT only
            Probe = new AudioProbeResult { FormatName = "m4a", ClaimedDurationMs = 2700, ClaimedChannels = 2 },
        };

        string id = await MakeImporter(decoder).ImportAsync(
            Request(source, title: "Split call", stereo: StereoMapping.Split),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("split", session!.ImportedSource!.ChannelMapping);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], session.Sources);
        float localPeak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        float remotePeak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(localPeak > 0.3f && remotePeak < 0.01f, $"local={localPeak} remote={remotePeak}");

        string id2 = await MakeImporter(decoder).ImportAsync(
            Request(source, title: "Swapped call", stereo: StereoMapping.SplitSwapped),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);
        var session2 = await new SessionStore(_paths.SessionJson(id2)).ReadAsync(default);
        Assert.Equal("split-swapped", session2!.ImportedSource!.ChannelMapping);
        float remotePeak2 = FlacPcmReader.ReadMono16k(_paths.AudioFile(id2, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(remotePeak2 > 0.3f, "swap: the left tone must land on the Remote leg");
    }

    /// <summary>IProgress that invokes inline (Progress&lt;T&gt; posts to a SynchronizationContext
    /// that unit tests do not have, making report order racy).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

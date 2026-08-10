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

    /// <summary>200 ms silence + tone + 1000 ms gap + tone + 1000 ms tail: EnergyProbe yields TWO
    /// segments, so a scripted engine can transcribe one and then fault.</summary>
    private string WriteTwoBurstWav(string name, int rate = 16000)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        int silence = rate / 5, speech = rate * 3 / 2, gap = rate, tail = rate;
        var buf = new float[silence + speech + gap + speech + tail];
        for (int f = 0; f < speech; f++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));
            buf[silence + f] = v;
            buf[silence + speech + gap + f] = v;
        }
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    private AudioImporter MakeImporter(FakeDecoder decoder, Settings? settings = null,
        IReadOnlySet<string>? models = null, IEngineFactory? engines = null, StoragePaths? paths = null,
        Func<string, long?>? volumeFreeBytes = null)
        => new(paths ?? _paths, settings ?? new Settings { Language = "en" }, decoder, engines ?? new EchoFactory(),
            () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), new FixedZoneTime(), appVersion: "0.2.0-test",
            availableModels: () => models ?? new HashSet<string> { "base.en", "tiny.en", "small.en" },
            volumeFreeBytes: volumeFreeBytes);

    private static ImportRequest Request(string sourcePath, string title = "Client call",
        StereoMapping stereo = StereoMapping.Downmix, string? model = null, string? language = null) => new()
    {
        SourcePath = sourcePath, Title = title,
        RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
        MatterIds = ["M-2026-001"], Stereo = stereo, Model = model, Language = language,
    };

    [Fact]
    public async Task Import_honors_an_explicit_model_override()
    {
        string source = Path.Combine(_root, "override.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-ov.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
        };

        // Global settings are Model=auto; the explicit "tiny.en" override must win.
        string id = await MakeImporter(decoder).ImportAsync(
            Request(source, model: "tiny.en", language: "en"),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("tiny.en", session!.Model);
        Assert.Equal("ggml-tiny.en.bin", session.WeightsFile);   // fake engine names the file from the model
    }

    [Fact]
    public async Task Import_with_no_override_uses_the_global_settings_model()
    {
        string source = Path.Combine(_root, "global.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-gl.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
        };

        string id = await MakeImporter(decoder, new Settings { Model = "base.en", Language = "en" })
            .ImportAsync(Request(source),   // Model/Language null -> global
                progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("base.en", session!.Model);
    }

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

    [Fact]
    public async Task Duration_mismatch_continue_writes_the_marker_and_flags_provenance()
    {
        string source = Path.Combine(_root, "lying.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-short.wav", 16000, 1, 0),   // ~2700 ms decoded
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 10_000 },
        };
        DurationMismatchInfo? seen = null;

        string id = await MakeImporter(decoder).ImportAsync(Request(source, title: "Mismatch"),
            progress: null,
            info => { seen = info; return Task.FromResult(true); },   // Continue
            CancellationToken.None);

        Assert.Equal(10_000, seen!.ClaimedDurationMs);
        Assert.InRange(seen.DecodedDurationMs, 2600, 2800);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        var marker = lines.Single(l => l.Kind == TranscriptKind.Marker
            && l.Text.StartsWith("imported audio duration mismatch", StringComparison.Ordinal));
        Assert.Equal(string.Format(Markers.ImportedDurationMismatch, "0:10",
            FormatShort(seen.DecodedDurationMs)), marker.Text);
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.True(session!.ImportedSource!.DurationMismatch);
        Assert.Equal(lines.Count(l => l.Kind == TranscriptKind.Marker), session.MarkerCount);
    }

    private static string FormatShort(long ms)
        => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Duration_mismatch_decline_deletes_the_partial_folder()
    {
        string source = Path.Combine(_root, "declined.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-decline.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 10_000 },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MakeImporter(decoder).ImportAsync(Request(source), progress: null,
                _ => Task.FromResult(false), CancellationToken.None));   // Cancel at the gate

        Assert.True(!Directory.Exists(_paths.SessionsDir)
            || !Directory.EnumerateDirectories(_paths.SessionsDir).Any());
        Assert.True(File.Exists(source));                                // original untouched
    }

    [Fact]
    public async Task Cancel_during_decode_deletes_the_partial_folder()
    {
        string source = Path.Combine(_root, "cancelled.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource();
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-cancel.wav", 16000, 1, 0),
            BeforeDecode = ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MakeImporter(decoder).ImportAsync(Request(source), progress: null,
                _ => Task.FromResult(true), cts.Token));

        Assert.True(!Directory.Exists(_paths.SessionsDir)
            || !Directory.EnumerateDirectories(_paths.SessionsDir).Any());
    }

    [Fact]
    public async Task Multichannel_downmixes_with_a_note_and_no_claim_means_no_gate()
    {
        string source = Path.Combine(_root, "surround.wma");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-4ch.wav", 16000, 4, 0, 1, 2, 3),
            Probe = new AudioProbeResult { FormatName = "wma", ClaimedDurationMs = null },   // no claim
        };
        bool confirmCalled = false;

        string id = await MakeImporter(decoder).ImportAsync(Request(source, title: "Surround"),
            progress: null,
            _ => { confirmCalled = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.False(confirmCalled);                                     // nothing to cross-check
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("downmix-multichannel", session!.ImportedSource!.ChannelMapping);
        Assert.Equal(4, session.ImportedSource.DecodedChannels);
        Assert.False(session.ImportedSource.DurationMismatch);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == string.Format(Markers.ImportedDownmixed, 4));
        Assert.Equal([SourceKind.Local], session.Sources);               // one downmixed leg
    }

    [Fact]
    public async Task Two_channel_downmix_writes_the_downmixed_marker_end_to_end()
    {
        // design 2026-07-29 follow-up 3. ChannelMapperDownmixMarkerTests pins Plan(2, Downmix).Downmixed
        // and the importer's append path is covered for >2 channels; this pins their COMPOSITION for
        // the 2-channel case - the primary path import-time speaker detection serves.
        string source = Path.Combine(_root, "twoparty.m4a");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-2ch.wav", 16000, 2, 0, 1),
            Probe = new AudioProbeResult { FormatName = "m4a", ClaimedDurationMs = null, ClaimedChannels = 2 },
        };

        string id = await MakeImporter(decoder).ImportAsync(
            Request(source, title: "Two-party", stereo: StereoMapping.Downmix),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("downmix", session!.ImportedSource!.ChannelMapping);
        Assert.Equal(2, session.ImportedSource.DecodedChannels);
        Assert.Equal([SourceKind.Local], session.Sources);            // one downmixed leg, not a split
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == string.Format(Markers.ImportedDownmixed, 2));
    }

    [Fact]
    public async Task Import_refuses_a_model_that_is_not_installed_before_creating_a_folder()
    {
        string source = Path.Combine(_root, "missing-model.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-mm.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700 },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeImporter(decoder, models: new HashSet<string> { "base.en" })   // turbo absent
                .ImportAsync(Request(source, model: "large-v3-turbo"),
                    progress: null, _ => Task.FromResult(true), CancellationToken.None));

        Assert.Contains("large-v3-turbo", ex.Message);
        Assert.Contains("is not downloaded", ex.Message);
        Assert.Contains("fetch-models.ps1", ex.Message);
        Assert.True(!Directory.Exists(_paths.SessionsDir)
            || !Directory.EnumerateDirectories(_paths.SessionsDir).Any());   // gated before any folder
        Assert.True(File.Exists(source));                                     // original untouched
    }

    [Fact]
    public async Task Import_medium_en_with_a_non_english_language_refuses_with_a_multilingual_hint()
    {
        string source = Path.Combine(_root, "spanish.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-es.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700 },
        };

        // medium.en present but NOT multilingual "medium": a non-English language strips
        // medium.en -> medium (BackendSelector), which is absent -> refuse with a routing hint.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeImporter(decoder, models: new HashSet<string> { "medium.en" })
                .ImportAsync(Request(source, model: "medium.en", language: "es"),
                    progress: null, _ => Task.FromResult(true), CancellationToken.None));

        Assert.Contains("English-only", ex.Message);
        Assert.Contains("large-v3-turbo", ex.Message);
    }

    [Fact]
    public async Task A_transcription_fault_keeps_the_session_its_audio_and_a_marker()
    {
        string source = Path.Combine(_root, "salvage.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteTwoBurstWav("decoded-salvage.wav"),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 5200, ClaimedChannels = 1 },
        };
        // First segment transcribes; the second faults - exactly as a missing-weights downgrade did.
        var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            new object[]
            {
                new TranscriptionResult("first segment survived", "en", 0.01),
                new InvalidOperationException("engine exploded mid-run"),
            }));

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, engines: engines)
            .ImportAsync(Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

        string sessionDir = Assert.Single(Directory.GetDirectories(_paths.SessionsDir));
        string id = Path.GetFileName(sessionDir);
        Assert.True(Directory.Exists(_paths.SourceDir(id)));                      // the archived copy survived
        Assert.True(File.Exists(_paths.TranscriptJsonl(id)));
        Assert.True(File.Exists(Path.Combine(sessionDir, "manifest.json")));      // finalized AND sealed

        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.NotNull(record!.EndedAtUtc);        // finalized: RecoveryScanner must NOT adopt it later

        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Segment
                                 && l.Text.Contains("first segment survived", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
                                 && l.Text.Contains(Markers.TranscriptionFailed, StringComparison.Ordinal));

        // The retained FLAC leg must survive too: RetranscriptionRunner (Resume/re-transcribe -
        // the whole reason salvage beats delete) gates its input legs on File.Exists over
        // _paths.AudioFile, so a salvaged session with no leg would be un-recoverable.
        Assert.True(File.Exists(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        Assert.Equal([SourceKind.Local], record.RetainedAudioSources);
    }

    [Fact]
    public async Task A_transcription_fault_with_retention_never_keeps_no_leg()
    {
        string source = Path.Combine(_root, "salvage-never.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteTwoBurstWav("decoded-salvage-never.wav"),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 5200, ClaimedChannels = 1 },
        };
        var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            new object[]
            {
                new TranscriptionResult("first segment survived", "en", 0.01),
                new InvalidOperationException("engine exploded mid-run"),
            }));
        var settings = new Settings { Language = "en", AudioRetention = "never" };

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, settings, engines: engines)
            .ImportAsync(Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

        // A user who opted out of audio retention must not suddenly get audio kept just because
        // the import faulted mid-transcription - the session and transcript still survive, but
        // no leg is written and RetainedAudioSources stays empty.
        string sessionDir = Assert.Single(Directory.GetDirectories(_paths.SessionsDir));
        string id = Path.GetFileName(sessionDir);
        Assert.False(File.Exists(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        Assert.False(File.Exists(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav)));
        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Empty(record!.RetainedAudioSources);
        Assert.NotNull(record.EndedAtUtc);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
                                 && l.Text.Contains(Markers.TranscriptionFailed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_leg_write_failure_during_salvage_still_finalizes_the_session()
    {
        // 2026-08-11 review I1: the retained-audio write can fail for the SAME reason the
        // ORIGINAL import fault happened (disk exhaustion is the likeliest real cause) - that
        // must not abort salvage before session.json gets EndedAtUtc, or RecoveryScanner.
        // FindUnendedAsync adopts the folder as a bogus "recovered" session at next startup.
        //
        // The session id is deterministic from RecordedAtLocal+title (SessionId.New, matching
        // SessionBootstrap's own algorithm), so FakeDecoder.BeforeDecode - which fires AFTER
        // SessionBootstrap has already created SessionDir(id), well before salvage runs - can
        // occupy the leg's destination with a DIRECTORY of the same name: AudioSinkFactory.
        // Create/WaveFileWriter cannot open a FileStream where a directory already exists.
        string title = "Leg write fails";
        string id = SessionId.New(
            new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)), AppKind.Manual, title);
        string source = Path.Combine(_root, "leg-write-fails.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteTwoBurstWav("decoded-leg-fail.wav"),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 5200, ClaimedChannels = 1 },
            BeforeDecode = _ =>
            {
                Directory.CreateDirectory(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav));
                return Task.CompletedTask;
            },
        };
        var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            new object[]
            {
                new TranscriptionResult("first segment survived", "en", 0.01),
                new InvalidOperationException("engine exploded mid-run"),
            }));
        var settings = new Settings { Language = "en", AudioFormat = AudioFormat.Wav };

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, settings, engines: engines)
            .ImportAsync(Request(source, title: title), null, _ => Task.FromResult(true), CancellationToken.None));

        // Finalization still completed despite the leg write failing: manifest sealed, EndedAtUtc
        // set, and RetainedAudioSources simply omits the leg that could not be written - rather
        // than the folder being left half-finalized (EndedAtUtc == null).
        Assert.True(File.Exists(Path.Combine(_paths.SessionDir(id), "manifest.json")));
        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.NotNull(record!.EndedAtUtc);
        Assert.Empty(record.RetainedAudioSources);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
                                 && l.Text.Contains(Markers.TranscriptionFailed, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_marker_append_failure_during_salvage_still_finalizes_the_session()
    {
        // 2026-08-11 review I1 round 2: the marker append is itself a disk write - the FIRST one
        // salvage attempts, sitting BEFORE the leg loop - and was unguarded. If it throws, it must
        // not prevent session.json from getting EndedAtUtc either: the identical bogus-recovered-
        // session bug as the leg-write case, just one step earlier.
        //
        // Same deterministic-id + BeforeDecode technique as the leg-write test, but this time the
        // DIRECTORY occupies transcript.jsonl's own path. File.Exists(directory) is false, so
        // TranscriptStore.ReadAllAsync/NextSeqAsync (both used by InitializeAsync and by salvage's
        // OWN lastMs read) see "no file yet" and succeed with an empty result; only the marker
        // APPEND (File.AppendAllTextAsync onto a directory) fails. The engine faults on the FIRST
        // (and only) segment - WriteBurstWav yields exactly one - so no real segment or marker is
        // EVER appended during transcription; the directory is untouched until salvage's own
        // marker append is the first thing to actually try writing there.
        string title = "Marker append fails";
        string id = SessionId.New(
            new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)), AppKind.Manual, title);
        string source = Path.Combine(_root, "marker-append-fails.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-marker-fail.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
            BeforeDecode = _ =>
            {
                Directory.CreateDirectory(_paths.TranscriptJsonl(id));
                return Task.CompletedTask;
            },
        };
        var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            new object[] { new InvalidOperationException("engine exploded immediately") }));

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, engines: engines)
            .ImportAsync(Request(source, title: title), null, _ => Task.FromResult(true), CancellationToken.None));

        // Finalization still completed despite the marker append failing: EndedAtUtc set and
        // manifest.json sealed (ManifestBuilder treats the transcript.jsonl DIRECTORY as absent
        // via its own File.Exists check and simply omits it, rather than throwing).
        Assert.True(File.Exists(Path.Combine(_paths.SessionDir(id), "manifest.json")));
        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.NotNull(record!.EndedAtUtc);
    }

    [Fact]
    public async Task A_two_leg_split_import_salvages_both_legs_and_updates_Sources()
    {
        // 2026-08-11 review I3: Sources was never updated on the salvage path, so a split-stereo
        // import that faults mid-transcription used to write RetainedAudioSources = [Local,
        // Remote] while Sources still said [Local] from bootstrap - a session.json contradicting
        // itself about which sides exist. Also the only coverage of the two-leg salvage path.
        string source = Path.Combine(_root, "split-salvage.m4a");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-split-salvage.wav", 16000, 2, 0, 1),   // tone both channels
            Probe = new AudioProbeResult { FormatName = "m4a", ClaimedDurationMs = 2700, ClaimedChannels = 2 },
        };
        // Local's one segment transcribes; Remote's one segment faults - proving BOTH legs still
        // salvage even though only one side ever reached the engine successfully.
        var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            new object[]
            {
                new TranscriptionResult("local side survived", "en", 0.01),
                new InvalidOperationException("engine exploded on the remote leg"),
            }));

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, engines: engines)
            .ImportAsync(Request(source, title: "Split salvage", stereo: StereoMapping.Split),
                null, _ => Task.FromResult(true), CancellationToken.None));

        string sessionDir = Assert.Single(Directory.GetDirectories(_paths.SessionsDir));
        string id = Path.GetFileName(sessionDir);
        Assert.True(File.Exists(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac)));
        Assert.True(File.Exists(_paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac)));

        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], record!.RetainedAudioSources);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], record.Sources);
    }

    [Fact]
    public async Task A_transcription_fault_stamps_decoded_provenance_on_the_salvaged_session()
    {
        // 2026-08-11 review I4: DecodedDurationMs/DecodedSampleRate/DecodedChannels/ChannelMapping
        // are non-nullable, so leaving them unstamped on salvage serialized as positive claims of
        // ZERO/"" on a record that simultaneously claims a decoded duration a few fields over.
        // The mismatch gate also fires (Continue) here so DurationMismatch is exercised too, not
        // just the zero-vs-real numeric fields.
        string source = Path.Combine(_root, "provenance-salvage.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteTwoBurstWav("decoded-provenance.wav"),   // ~5200 ms decoded truth
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 20_000, ClaimedChannels = 1 },
        };
        var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
            new object[]
            {
                new TranscriptionResult("first segment survived", "en", 0.01),
                new InvalidOperationException("engine exploded mid-run"),
            }));

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, engines: engines)
            .ImportAsync(Request(source, title: "Provenance salvage"), null,
                _ => Task.FromResult(true),   // Continue past the mismatch gate
                CancellationToken.None));

        string sessionDir = Assert.Single(Directory.GetDirectories(_paths.SessionsDir));
        string id = Path.GetFileName(sessionDir);
        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        var imported = record!.ImportedSource;
        Assert.NotNull(imported);
        Assert.InRange(imported!.DecodedDurationMs, 5000, 5400);      // NOT the non-nullable zero default
        Assert.Equal(16000, imported.DecodedSampleRate);
        Assert.Equal(1, imported.DecodedChannels);
        Assert.Equal("mono", imported.ChannelMapping);
        Assert.True(imported.DurationMismatch);
    }

    [Fact]
    public async Task A_failure_BEFORE_any_audio_is_written_still_deletes_the_folder()
    {
        string source = Path.Combine(_root, "early-fail.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-early.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
            // Dies during decode - before ChannelMapper writes any leg, so nothing is worth keeping.
            BeforeDecode = _ => throw new InvalidDataException("decode blew up"),
        };

        await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder)
            .ImportAsync(Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

        Assert.Empty(Directory.GetDirectories(_paths.SessionsDir));
    }

    [Fact]
    public async Task An_unwritable_storage_root_fails_before_any_copy_with_an_actionable_message()
    {
        string source = Path.Combine(_root, "unwritable.mp3");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        // A FILE where a directory would have to go: CreateDirectory under it always throws IOException.
        string blocker = Path.Combine(_root, "blocker.txt");
        await File.WriteAllTextAsync(blocker, "x");
        var badPaths = new StoragePaths(Path.Combine(blocker, "store"));
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-unwritable.wav", 16000, 1, 0),
            Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MakeImporter(decoder, paths: badPaths).ImportAsync(
                Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

        Assert.Contains("storage", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(blocker, "store")));
    }

    // 2026-08-11 coordinator review round 1: the old sourceLength*2 blended estimate OVER-refused
    // a WAV source (FfmpegAudioDecoder.DecodeAsync short-circuits WAV and writes no new decoded
    // bytes - real need is about 1x the archived copy plus a small 16 kHz leg) on a disk that had
    // enough room for the copy but less than 2x - a legitimate import the fix must let through.
    [Fact]
    public async Task A_wav_source_imports_when_free_space_is_between_1x_and_2x_source_length()
    {
        string decodedPath = WriteBurstWav("sizable-decoded.wav", 16000, 1, 0);
        string source = Path.Combine(_root, "sizable.wav");
        File.Copy(decodedPath, source);
        long sourceLength = new FileInfo(source).Length;

        var decoder = new FakeDecoder
        {
            DecodedWavPath = decodedPath,   // WAV pass-through: DecodeAsync writes no new bytes
            Probe = new AudioProbeResult
            {
                FormatName = "wav", ClaimedDurationMs = 2700, ClaimedChannels = 1, ClaimedSampleRate = 16000,
            },
        };
        // Between 1x (the archived copy alone) and 2x (the OLD, over-estimating threshold this
        // task fixes): the pre-fix code computed needBytes = sourceLength*2 and would have
        // refused this exact, genuinely-sufficient disk.
        long available = sourceLength + sourceLength * 9 / 10;   // 1.9x
        var importer = MakeImporter(decoder, volumeFreeBytes: _ => available);

        string id = await importer.ImportAsync(
            Request(source), null, _ => Task.FromResult(true), CancellationToken.None);

        Assert.True(Directory.Exists(_paths.SessionDir(id)));
    }

    // ALSO FIX (2026-08-11 coordinator review round 1): AvailableFreeSpace can throw even after
    // IsReady answered true (a mapped network drive dropping mid-check) - the widened guard must
    // treat "cannot be determined" the same way regardless of which step failed: skip, not fatal.
    [Fact]
    public async Task An_unavailable_free_space_reading_is_skipped_rather_than_fatal()
    {
        string source = Path.Combine(_root, "unknown-free-space.mp3");
        await File.WriteAllBytesAsync(source, new byte[1024]);
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-unknown-space.wav", 16000, 1, 0),
            Probe = new AudioProbeResult
            {
                FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1, ClaimedSampleRate = 16000,
            },
        };
        // null = "cannot be determined", however that happens in production - never fatal.
        var importer = MakeImporter(decoder, volumeFreeBytes: _ => null);

        string id = await importer.ImportAsync(
            Request(source), null, _ => Task.FromResult(true), CancellationToken.None);

        Assert.True(Directory.Exists(_paths.SessionDir(id)));
    }

    /// <summary>IProgress that invokes inline (Progress&lt;T&gt; posts to a SynchronizationContext
    /// that unit tests do not have, making report order racy).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

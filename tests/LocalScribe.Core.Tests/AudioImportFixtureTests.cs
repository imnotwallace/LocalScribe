using System.Diagnostics;
using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using NAudio.Wave;

namespace LocalScribe.Core.Tests;

/// <summary>Design 2026-07-13 section 4.5's one real-FFmpeg test: a generated stereo tone is
/// encoded to a tiny real MP3 BY the fetched ffmpeg, then imported end-to-end through the real
/// FfmpegAudioDecoder (probe + subprocess decode) and AudioImporter (echo engine - no Whisper
/// model needed). Repo convention (GoldenCorpus/Diarisation): throws FileNotFoundException with
/// the fetch instruction when FFmpeg is absent; excluded by "Category!=Fixture" gates. NOTE:
/// under an isolated BaseOutputPath the repo walk cannot find tools\ffmpeg - set
/// LOCALSCRIBE_FFMPEG or run from the normal bin.</summary>
[Trait("Category", "Fixture")]
public sealed class AudioImportFixtureTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-import-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public AudioImportFixtureTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new StoragePaths(Path.Combine(_root, "store"));
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class EnergyProbe : ISpeechProbabilityModel
    {
        public float SpeechProbability(ReadOnlySpan<float> window)
            => Pipeline.SegmentAudio.RmsDb(window) > -30.0 ? 0.95f : 0.02f;
        public void Reset() { }
    }

    private sealed class EchoFactory : IEngineFactory
    {
        public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language, string? prompt, CancellationToken ct)
            => Task.FromResult<ITranscriptionEngine>(new FakeTranscriptionEngine(plan.ModelName,
                s => new Transcription.TranscriptionResult($"[{s.Source} {s.StartMs}-{s.EndMs}]", "en", 0.01)));
    }

    /// <summary>Locates the real bundled ffmpeg/ffprobe, or throws with the fetch instruction -
    /// the repo convention (GoldenCorpus/Diarisation) for a fixture that needs a real tool on
    /// disk. Shared by every fact in this file so the check and its message stay in one place.</summary>
    private static string RequireFfmpegTools()
    {
        string? tools = FfmpegLocator.FindToolsDir();
        return tools ?? throw new FileNotFoundException(
            "FFmpeg missing. Run tools/fetch-ffmpeg.ps1 (two-run pin flow), or set LOCALSCRIBE_FFMPEG.");
    }

    /// <summary>A short WAV fixture at the given channel count (200 ms silence + 1500 ms tone in
    /// EVERY channel + 1000 ms tail, 44.1 kHz) - real ffmpeg then muxes/encodes these into the
    /// container each fact under test actually exercises.</summary>
    private string WriteFixtureWav(string name, int channels)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, channels));
        int silence = 8820, speech = 66150, tail = 44100;
        var buf = new float[(silence + speech + tail) * channels];
        for (int f = 0; f < speech; f++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / 44100.0));
            for (int c = 0; c < channels; c++)
                buf[(silence + f) * channels + c] = v;
        }
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    /// <summary>The AudioImporter every fact in this file drives against the REAL FfmpegAudioDecoder
    /// (echo transcription engine - no Whisper model needed).</summary>
    private AudioImporter MakeFixtureImporter()
        => new(_paths, new Settings { Language = "en" },
            new FfmpegAudioDecoder(RequireFfmpegTools()), new EchoFactory(), () => new EnergyProbe(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), TimeProvider.System, "fixture",
            availableModels: () => new HashSet<string> { "tiny.en", "base.en", "small.en" });

    [Fact]
    public async Task RealFfmpeg_imports_a_generated_stereo_mp3_end_to_end()
    {
        string tools = RequireFfmpegTools();

        // Generate the source: 200 ms silence + 1500 ms LEFT-only tone + 1000 ms silence, stereo
        // 44.1 kHz, then let the REAL ffmpeg encode the tiny MP3 (LGPL builds include libmp3lame).
        string wav = Path.Combine(_root, "tone.wav");
        using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
        {
            int silence = 8820, speech = 66150, tail = 44100;
            var buf = new float[(silence + speech + tail) * 2];
            for (int f = 0; f < speech; f++)
                buf[(silence + f) * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / 44100.0));
            w.WriteSamples(buf, 0, buf.Length);
        }
        string mp3 = Path.Combine(_root, "phone recording.mp3");
        var encode = Process.Start(new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"),
            $"-v error -nostdin -y -i \"{wav}\" -codec:a libmp3lame -b:a 64k \"{mp3}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await encode.WaitForExitAsync();
        Assert.Equal(0, encode.ExitCode);

        // MP3 encoder padding can push claimed-vs-decoded past 1 percent on a 2.7 s file, so the
        // gate MAY fire - always Continue; do not assert DurationMismatch either way.
        string id = await MakeFixtureImporter().ImportAsync(new ImportRequest
        {
            SourcePath = mp3, Title = "Fixture call",
            RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 5, 14, 30, 0))),
            Stereo = StereoMapping.Split,
        }, progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("imported", session!.Origin);
        Assert.Equal("phone recording.mp3", session.ImportedSource!.FileName);
        string mp3ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(mp3)));
        Assert.Equal(mp3ExpectedSha256, session.ImportedSource.Sha256);
        Assert.Contains("mp3", session.ImportedSource.ContainerFormat);
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);   // decoded-stream truth
        Assert.Equal(2, session.ImportedSource.DecodedChannels);
        Assert.Equal("split", session.ImportedSource.ChannelMapping);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2400, 3200);

        float localPeak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        float remotePeak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(localPeak > 0.2f && remotePeak < 0.05f, $"local={localPeak} remote={remotePeak}");
        Assert.True(File.Exists(_paths.TranscriptMd(id)));
        Assert.True(session.SegmentCount >= 1);
    }

    [Fact]
    public async Task RealFfmpeg_imports_a_generated_mp4_video_end_to_end()
    {
        string tools = RequireFfmpegTools();

        // Generate the source: 200 ms silence + 1500 ms tone + 1000 ms silence, mono 44.1 kHz,
        // then let the REAL ffmpeg mux it against a synthetic black video track (native mpeg4
        // encoder - the BtbN LGPL SHARED build tools/fetch-ffmpeg.ps1 pins has no GPL libx264, so
        // this is the only video encoder available, and it is enough: we never need to PLAY the
        // video, only prove it gets stripped) into a tiny real MP4 with two streams. This is the
        // feature's whole premise ("we simply only extract the audio channel") - proving the
        // EXISTING decode command already excludes the video track with no production change.
        string wav = Path.Combine(_root, "tone.wav");
        using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)))
        {
            int silence = 8820, speech = 66150, tail = 44100;
            var buf = new float[silence + speech + tail];
            for (int f = 0; f < speech; f++)
                buf[silence + f] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / 44100.0));
            w.WriteSamples(buf, 0, buf.Length);
        }
        string mp4 = Path.Combine(_root, "meeting recording.mp4");
        var encode = Process.Start(new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"),
            $"-v error -nostdin -y -i \"{wav}\" -f lavfi -i \"color=c=black:s=64x64:r=1\" " +
            $"-shortest -c:v mpeg4 -pix_fmt yuv420p -c:a aac -b:a 96k \"{mp4}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await encode.WaitForExitAsync();
        Assert.Equal(0, encode.ExitCode);

        string id = await MakeFixtureImporter().ImportAsync(new ImportRequest
        {
            SourcePath = mp4, Title = "Fixture video call",
            RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 5, 14, 30, 0))),
            Stereo = StereoMapping.Downmix,
        }, progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("imported", session!.Origin);
        // Provenance is the ORIGINAL video file's own name/hash (design 2026-07-13 section 4) -
        // never renamed, never pointed at the audio-only decode byproduct.
        Assert.Equal("meeting recording.mp4", session.ImportedSource!.FileName);
        string mp4ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(mp4)));
        Assert.Equal(mp4ExpectedSha256, session.ImportedSource.Sha256);
        Assert.Contains("mp4", session.ImportedSource.ContainerFormat);
        Assert.Equal(1, session.ImportedSource.DecodedChannels);     // audio-only: the video track never reaches here
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);
        Assert.Equal("mono", session.ImportedSource.ChannelMapping);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2400, 3000);

        float peak = FlacPcmReader.ReadMono16k(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(peak > 0.2f, $"peak={peak}");
        Assert.True(File.Exists(_paths.TranscriptMd(id)));
        Assert.True(session.SegmentCount >= 1);
    }

    [Fact]
    [Trait("Category", "Fixture")]
    public async Task RealFfmpeg_decodes_the_SAME_audio_stream_it_probed()
    {
        string tools = RequireFfmpegTools();          // existing helper in this file
        string mono = WriteFixtureWav("track-a.wav", channels: 1);
        string stereo = WriteFixtureWav("track-b.wav", channels: 2);

        // Stream order: 0:v video, 0:a:0 MONO, 0:a:1 STEREO. ffmpeg's OWN automatic (no -map)
        // audio selection follows each stream's "default" disposition flag, NOT raw channel count
        // directly - MEASURED (2026-08-11): the mp4 muxer auto-sets "default" on whichever audio
        // stream is mapped FIRST, so a plain "-map 0:a -map 1:a" with no explicit disposition
        // leaves the flag on the mono stream and ffmpeg's automatic pick then agrees with
        // ParseProbeJson by accident - a vacuous repro. -disposition:a:0 0 -disposition:a:1
        // default explicitly clears it from the first (mono) output audio stream and sets it on
        // the second (stereo), which is what actually makes ffmpeg's default-selection diverge
        // from the probe's first-audio-stream pick, matching a real multi-track file where the
        // higher-quality/secondary track is flagged default while track order still puts a lower
        // one first.
        string mp4 = Path.Combine(_root, "two track bodycam.mp4");
        var encode = Process.Start(new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"),
            $"-v error -nostdin -y -i \"{mono}\" -i \"{stereo}\" " +
            $"-f lavfi -i \"color=c=black:s=64x64:r=1\" -shortest " +
            $"-map 2:v -map 0:a -map 1:a -c:v mpeg4 -pix_fmt yuv420p -c:a aac -b:a 96k " +
            $"-disposition:a:0 0 -disposition:a:1 default \"{mp4}\"")
            { UseShellExecute = false })!;
        await encode.WaitForExitAsync();
        Assert.Equal(0, encode.ExitCode);

        string id = await MakeFixtureImporter().ImportAsync(
            new ImportRequest
            {
                SourcePath = mp4, Title = "Two-track fixture",
                RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
                MatterIds = [], Stereo = StereoMapping.Downmix,
            },
            null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        // The recorded channel count must describe the stream that was ACTUALLY decoded. Before the
        // fix this records 1 (probed from stream 0) while ffmpeg decoded the 2-channel stream.
        Assert.Equal(1, session!.ImportedSource!.DecodedChannels);
        Assert.Equal("mono", session.ImportedSource.ChannelMapping);
        // The chosen-track provenance (2026-08-11): the first audio stream is audio-relative
        // index 0 among "-map 0:a:n", and it is recorded regardless of decode success.
        Assert.Equal(0, session.ImportedSource.AudioStreamIndex);
    }
}

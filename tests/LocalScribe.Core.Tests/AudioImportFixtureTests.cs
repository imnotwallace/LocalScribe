using System.Diagnostics;
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
    public AudioImportFixtureTests() => Directory.CreateDirectory(_root);
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

    [Fact]
    public async Task RealFfmpeg_imports_a_generated_stereo_mp3_end_to_end()
    {
        string? tools = FfmpegLocator.FindToolsDir();
        if (tools is null)
            throw new FileNotFoundException(
                "FFmpeg missing. Run tools/fetch-ffmpeg.ps1 (two-run pin flow), or set LOCALSCRIBE_FFMPEG.");

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

        var paths = new StoragePaths(Path.Combine(_root, "store"));
        var importer = new AudioImporter(paths, new Settings { Language = "en" },
            new FfmpegAudioDecoder(tools), new EchoFactory(), () => new EnergyProbe(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), TimeProvider.System, "fixture");

        // MP3 encoder padding can push claimed-vs-decoded past 1 percent on a 2.7 s file, so the
        // gate MAY fire - always Continue; do not assert DurationMismatch either way.
        string id = await importer.ImportAsync(new ImportRequest
        {
            SourcePath = mp3, Title = "Fixture call",
            RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 5, 14, 30, 0))),
            Stereo = StereoMapping.Split,
        }, progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("imported", session!.Origin);
        Assert.Equal("phone recording.mp3", session.ImportedSource!.FileName);
        Assert.Contains("mp3", session.ImportedSource.ContainerFormat);
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);   // decoded-stream truth
        Assert.Equal(2, session.ImportedSource.DecodedChannels);
        Assert.Equal("split", session.ImportedSource.ChannelMapping);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2400, 3200);

        float localPeak = FlacPcmReader.ReadMono16k(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        float remotePeak = FlacPcmReader.ReadMono16k(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(localPeak > 0.2f && remotePeak < 0.05f, $"local={localPeak} remote={remotePeak}");
        Assert.True(File.Exists(paths.TranscriptMd(id)));
        Assert.True(session.SegmentCount >= 1);
    }
}

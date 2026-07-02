using System.Text.Json;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

[Trait("Category", "Fixture")]
public class GoldenCorpusFixtureTests
{
    private const double Epsilon = 0.05;

    private static OfflinePipelineRunner RealRunner(StoragePaths paths, Settings settings) =>
        new(paths, settings, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, Environment.ProcessorCount / 2)),
            new StopwatchClock(), TimeProvider.System, "fixture");

    [Fact]
    public async Task Silence_produces_zero_segments_hard_bar()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string wav = Path.Combine(root, "silence.wav");
            using (var sink = new WavSink(wav)) sink.Write(new float[16000 * 30]);   // 30 s silence

            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { Model = "base.en", AudioFormat = AudioFormat.Wav };
            string id = await RealRunner(paths, settings).RunAsync(
                new OfflineRunOptions { LocalWavPath = wav }, default);

            var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
            Assert.Empty(lines);                          // zero hallucination on silence
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Golden_pair_wer_stays_at_baseline()
    {
        string golden = Path.Combine(ModelPaths.ModelsRoot, "golden");
        string localWav = Path.Combine(golden, "local.wav");
        string remoteWav = Path.Combine(golden, "remote.wav");
        if (!File.Exists(localWav) || !File.Exists(remoteWav))
            throw new FileNotFoundException(
                $"Golden corpus missing under {golden} - see docs/plans/2026-07-02-stage-2b-golden-corpus.md");

        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var paths = new StoragePaths(Path.Combine(root, "store"));
            var settings = new Settings { Model = "base.en", AudioFormat = AudioFormat.Wav };
            string id = await RealRunner(paths, settings).RunAsync(
                new OfflineRunOptions { LocalWavPath = localWav, RemoteWavPath = remoteWav }, default);

            var lines = await new TranscriptStore(paths.TranscriptJsonl(id)).ReadAllAsync(default);
            string HypFor(TranscriptSource src) => string.Join(" ",
                lines.Where(l => l.Kind == TranscriptKind.Segment && l.Source == src).Select(l => l.Text));

            double werLocal = WerCalculator.Wer(
                await File.ReadAllTextAsync(Path.Combine(golden, "reference-local.txt")),
                HypFor(TranscriptSource.Local));
            double werRemote = WerCalculator.Wer(
                await File.ReadAllTextAsync(Path.Combine(golden, "reference-remote.txt")),
                HypFor(TranscriptSource.Remote));

            string baselinePath = Path.Combine(golden, "baseline.json");
            if (!File.Exists(baselinePath))
            {
                await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(
                    new { werLocal, werRemote }, new JsonSerializerOptions { WriteIndented = true }));
                Assert.Fail($"Baseline recorded (local={werLocal:F3}, remote={werRemote:F3}) - re-run to assert.");
            }

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
            double baseLocal = doc.RootElement.GetProperty("werLocal").GetDouble();
            double baseRemote = doc.RootElement.GetProperty("werRemote").GetDouble();
            Assert.True(werLocal <= baseLocal + Epsilon, $"Local WER regressed: {werLocal:F3} > {baseLocal:F3}+{Epsilon}");
            Assert.True(werRemote <= baseRemote + Epsilon, $"Remote WER regressed: {werRemote:F3} > {baseRemote:F3}+{Epsilon}");
        }
        finally { Directory.Delete(root, true); }
    }
}

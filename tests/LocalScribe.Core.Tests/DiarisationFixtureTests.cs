using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Transcription;

/// <summary>Opt-in DER (Diarisation Error Rate) regression harness (Stage 5 Task 10), mirroring
/// <see cref="GoldenCorpusFixtureTests"/>'s shape exactly: it resolves a privileged, never-committed
/// fixture under <c>models/diar-fixture/</c> and throws <see cref="FileNotFoundException"/> when it
/// is absent, so the model-free gate (<c>dotnet test --filter "Category!=Fixture"</c>) never runs
/// it. The fixture is any privileged multi-speaker leg (<c>leg.flac</c> - either side, the helper
/// ignores <see cref="SourceKind"/>); the harness asserts both the auto-cluster-count path and the
/// forced-2 path against separate per-mode baselines recorded in <c>baseline.json</c>. The
/// <c>LocalScribe.Diarizer.exe</c> beside the test binary must come from a self-contained
/// single-file publish (<c>dotnet publish src/LocalScribe.Diarizer -c Debug -r win-x64
/// -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true</c>), copying ONLY that
/// one .exe here - a plain Debug build's apphost cannot run standalone (it needs its companion
/// .dll/.deps.json/.runtimeconfig.json on disk beside it), and copying its whole output folder
/// instead overwrites Core.Tests' own onnxruntime.dll (Silero VAD, 1.22.x) with sherpa's
/// incompatible 1.24.x build at the same relative path - see
/// docs/plans/2026-07-04-stage-5-diarisation-plan.md section 9 for the corpus provenance.</summary>
[Trait("Category", "Fixture")]
public class DiarisationFixtureTests
{
    private const double Epsilon = 0.05;

    [Fact]
    public async Task Der_within_baseline_plus_epsilon()
    {
        string legPath = ModelPaths.Resolve(Path.Combine("diar-fixture", "leg.flac"));
        if (!File.Exists(legPath))
            throw new FileNotFoundException(
                "Diarisation fixture missing. Copy a real multi-speaker leg as models/diar-fixture/leg.flac (privileged, never committed).", legPath);

        string fixtureDir = Path.GetDirectoryName(legPath)!;
        string referencePath = Path.Combine(fixtureDir, "reference.rttm");
        if (!File.Exists(referencePath))
            throw new FileNotFoundException(
                "Diarisation fixture reference labels missing. Copy reference.rttm alongside leg.flac into models/diar-fixture/ (privileged, never committed).", referencePath);

        string segModel = ModelPaths.Require(
            Path.Combine("sherpa-onnx-pyannote-segmentation-3-0", "model.onnx"));
        string embModel = ModelPaths.Require(
            "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx");

        string exePath = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                "LocalScribe.Diarizer.exe missing beside the test binary - publish it self-contained single-file (dotnet publish src/LocalScribe.Diarizer -c Debug -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true) and copy ONLY the single .exe here. A plain Debug build will not run standalone (framework-dependent apphost) and copying its folder overwrites Core.Tests' own onnxruntime.dll (Silero VAD, 1.22) with sherpa's 1.24.", exePath);

        var engine = new SherpaHelperDiariser(new FixtureProcessDiarisationHelper(exePath));
        var reference = RttmReader.Read(referencePath);

        var autoResult = await engine.DiariseAsync(
            new DiarisationRequest(legPath, SourceKind.Remote, segModel, embModel, ForcedClusterCount: null),
            new Progress<double>(_ => { }), default);
        double autoDer = DiarisationErrorRate.Compute(autoResult.Segments, reference);

        var forcedResult = await engine.DiariseAsync(
            new DiarisationRequest(legPath, SourceKind.Remote, segModel, embModel, ForcedClusterCount: 2),
            new Progress<double>(_ => { }), default);
        double forced2Der = DiarisationErrorRate.Compute(forcedResult.Segments, reference);

        string baselinePath = Path.Combine(fixtureDir, "baseline.json");
        if (!File.Exists(baselinePath))
        {
            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(
                new { autoDer, forced2Der }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Fail($"Baseline recorded (autoDer={autoDer:F3}, forced2Der={forced2Der:F3}) - re-run to assert.");
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
        double autoBaseline = doc.RootElement.GetProperty("autoDer").GetDouble();
        double forcedBaseline = doc.RootElement.GetProperty("forced2Der").GetDouble();
        Assert.True(autoDer <= autoBaseline + Epsilon,
            $"auto DER regressed: {autoDer:F3} > {autoBaseline:F3}+{Epsilon}");
        Assert.True(forced2Der <= forcedBaseline + Epsilon,
            $"forced-2 DER regressed: {forced2Der:F3} > {forcedBaseline:F3}+{Epsilon}");
    }

    /// <summary>Fixture-only duplicate of the production
    /// <c>LocalScribe.App.Services.ProcessDiarisationHelper</c> spawn/JSON-line protocol.
    /// LocalScribe.Core.Tests deliberately does not take a project reference to LocalScribe.App (a
    /// WPF app, plus the same ORT-isolation reasoning that keeps App from referencing
    /// LocalScribe.Diarizer directly) just for one opt-in test, so the minimal out-of-process
    /// mechanics are reproduced here. Keep this in lockstep with the production helper by hand if
    /// the wire contract in DiarisationWire.cs ever changes.</summary>
    private sealed class FixtureProcessDiarisationHelper(string exePath) : IDiarisationHelper
    {
        public Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct) =>
            RunProcessAsync(JsonSerializer.Serialize(job, DiarisationJson.Options), onStdoutLine, ct);

        public Task<int> RunEmbedAsync(EmbedJob job, Action<string> onStdoutLine, CancellationToken ct) =>
            RunProcessAsync(JsonSerializer.Serialize(job, DiarisationJson.Options), onStdoutLine, ct);

        private async Task<int> RunProcessAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start diarizer");
            await using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch { /* best-effort: the process may have exited between the check and the kill */ }
            });

            await proc.StandardInput.WriteAsync(jobJson);
            proc.StandardInput.Close();

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
                onStdoutLine(line);

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }
    }

    /// <summary>Minimal RTTM reader: pulls the fields this harness needs (start, duration, speaker
    /// name) from `SPEAKER` rows and ignores every other RTTM row type/field - sufficient for a
    /// hand-labelled reference file, not a general-purpose RTTM parser.</summary>
    private static class RttmReader
    {
        public static List<(double StartS, double EndS, string Speaker)> Read(string path)
        {
            var segments = new List<(double StartS, double EndS, string Speaker)>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || !line.StartsWith("SPEAKER", StringComparison.Ordinal)) continue;

                // RTTM columns: SPEAKER file-id channel start dur ortho speaker-type speaker-name conf slat
                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                double start = double.Parse(parts[3], CultureInfo.InvariantCulture);
                double dur = double.Parse(parts[4], CultureInfo.InvariantCulture);
                string speaker = parts[7];
                segments.Add((start, start + dur, speaker));
            }
            return segments;
        }
    }

    /// <summary>Frame-based Diarisation Error Rate: a 10 ms grid, single-active-speaker-per-frame
    /// on each side (matches this system's per-source VAD assumption - diarisation never models
    /// overlapping speech within one leg). Hypothesis clusters are matched to reference speakers
    /// with a greedy largest-overlap-first one-to-one assignment, adequate for the small, fixed
    /// speaker counts this system ever diarises (not a full Hungarian-algorithm solver).</summary>
    private static class DiarisationErrorRate
    {
        private const double FrameSeconds = 0.01;

        public static double Compute(
            IReadOnlyList<DiarisedSegment> hypothesis,
            IReadOnlyList<(double StartS, double EndS, string Speaker)> reference)
        {
            double refEnd = reference.Count == 0 ? 0.0 : reference.Max(r => r.EndS);
            double hypEnd = hypothesis.Count == 0 ? 0.0 : hypothesis.Max(h => h.EndMs / 1000.0);
            int frameCount = (int)Math.Ceiling(Math.Max(refEnd, hypEnd) / FrameSeconds);
            if (frameCount == 0) return 0.0;

            var refBySpeaker = new Dictionary<string, bool[]>(StringComparer.Ordinal);
            foreach (var seg in reference)
            {
                if (!refBySpeaker.TryGetValue(seg.Speaker, out var mask))
                    refBySpeaker[seg.Speaker] = mask = new bool[frameCount];
                MarkActive(mask, seg.StartS, seg.EndS, frameCount);
            }

            var hypByCluster = new Dictionary<int, bool[]>();
            foreach (var seg in hypothesis)
            {
                if (!hypByCluster.TryGetValue(seg.Cluster, out var mask))
                    hypByCluster[seg.Cluster] = mask = new bool[frameCount];
                MarkActive(mask, seg.StartMs / 1000.0, seg.EndMs / 1000.0, frameCount);
            }

            var overlap = new Dictionary<(string Speaker, int Cluster), int>();
            foreach (var (speaker, refMask) in refBySpeaker)
                foreach (var (cluster, hypMask) in hypByCluster)
                {
                    int count = 0;
                    for (int f = 0; f < frameCount; f++) if (refMask[f] && hypMask[f]) count++;
                    overlap[(speaker, cluster)] = count;
                }

            var matchedCluster = new Dictionary<string, int>(StringComparer.Ordinal);
            var usedClusters = new HashSet<int>();
            foreach (var pair in overlap
                         .OrderByDescending(kv => kv.Value)
                         .ThenBy(kv => kv.Key.Speaker, StringComparer.Ordinal)
                         .ThenBy(kv => kv.Key.Cluster))
            {
                if (pair.Value == 0) continue;
                var (speaker, cluster) = pair.Key;
                if (matchedCluster.ContainsKey(speaker) || usedClusters.Contains(cluster)) continue;
                matchedCluster[speaker] = cluster;
                usedClusters.Add(cluster);
            }

            long miss = 0, falseAlarm = 0, confusion = 0, totalRef = 0;
            for (int f = 0; f < frameCount; f++)
            {
                string? activeSpeaker = null;
                foreach (var (speaker, mask) in refBySpeaker) if (mask[f]) { activeSpeaker = speaker; break; }
                int? activeCluster = null;
                foreach (var (cluster, mask) in hypByCluster) if (mask[f]) { activeCluster = cluster; break; }

                if (activeSpeaker is null)
                {
                    if (activeCluster is not null) falseAlarm++;
                    continue;
                }

                totalRef++;
                if (activeCluster is null) { miss++; continue; }
                if (!matchedCluster.TryGetValue(activeSpeaker, out int expectedCluster) ||
                    expectedCluster != activeCluster.Value)
                    confusion++;
            }

            return totalRef == 0 ? 0.0 : (double)(miss + falseAlarm + confusion) / totalRef;
        }

        private static void MarkActive(bool[] mask, double startS, double endS, int frameCount)
        {
            int from = Math.Max(0, (int)(startS / FrameSeconds));
            int to = Math.Min(frameCount, (int)Math.Ceiling(endS / FrameSeconds));
            for (int f = from; f < to; f++) mask[f] = true;
        }
    }
}

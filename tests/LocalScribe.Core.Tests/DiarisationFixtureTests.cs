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
/// it. Recording a real multi-remote-speaker corpus is an open user action (see
/// docs/plans/2026-07-04-stage-5-diarisation-plan.md section 9) - this harness ships now so the
/// regression check is ready the moment that corpus exists.</summary>
[Trait("Category", "Fixture")]
public class DiarisationFixtureTests
{
    private const double Epsilon = 0.05;

    [Fact]
    public async Task Der_within_baseline_plus_epsilon()
    {
        string legPath = ModelPaths.Resolve(Path.Combine("diar-fixture", "remote.flac"));
        if (!File.Exists(legPath))
            throw new FileNotFoundException(
                "Diarisation fixture missing. Copy a real multi-remote-speaker leg + labels into models/diar-fixture/ (privileged, never committed).", legPath);

        string fixtureDir = Path.GetDirectoryName(legPath)!;
        string referencePath = Path.Combine(fixtureDir, "reference.rttm");
        if (!File.Exists(referencePath))
            throw new FileNotFoundException(
                "Diarisation fixture reference labels missing. Copy reference.rttm alongside remote.flac into models/diar-fixture/ (privileged, never committed).", referencePath);

        // The real Apache-2.0/MIT models fetch-models.ps1 pulls (see README) - required
        // regardless of the fixture, so a missing-models box fails with ModelPaths.Require's own
        // "run tools/fetch-models.ps1" message rather than a confusing downstream helper crash.
        string segModel = ModelPaths.Require(
            Path.Combine("sherpa-onnx-pyannote-segmentation-3-0", "model.onnx"));
        string embModel = ModelPaths.Require(
            "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx");

        // LocalScribe.Diarizer.exe is resolved exactly the way CompositionRoot.Build() resolves it
        // for the real app: beside this binary's own base directory. Nothing here builds or copies
        // it automatically (same ORT-isolation rationale as LocalScribe.App.csproj's long comment -
        // a same-folder copy of Diarizer's full build output collides with Silero VAD's own
        // onnxruntime.dll). A dev exercising this fixture publishes the helper once per the Stage 5
        // smoke runbook's prerequisite section and copies just the single .exe here.
        string exePath = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                "LocalScribe.Diarizer.exe missing beside the test binary - publish it per the Stage 5 smoke runbook (docs/plans/2026-07-04-stage-5-smoke-runbook.md) and copy the single .exe here.", exePath);

        var engine = new SherpaHelperDiariser(new FixtureProcessDiarisationHelper(exePath));
        var request = new DiarisationRequest(legPath, SourceKind.Remote, segModel, embModel, ForcedClusterCount: null);
        var result = await engine.DiariseAsync(request, new Progress<double>(_ => { }), default);

        var reference = RttmReader.Read(referencePath);
        double der = DiarisationErrorRate.Compute(result.Segments, reference);

        string baselinePath = Path.Combine(fixtureDir, "baseline.json");
        if (!File.Exists(baselinePath))
        {
            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(
                new { der }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Fail($"Baseline recorded (der={der:F3}) - re-run to assert.");
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
        double baseline = doc.RootElement.GetProperty("der").GetDouble();
        Assert.True(der <= baseline + Epsilon, $"DER regressed: {der:F3} > {baseline:F3}+{Epsilon}");
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
        public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
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

            await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(job, DiarisationJson.Options));
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

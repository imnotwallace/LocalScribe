using LocalScribe.Core.Diarisation;
using SherpaOnnx;

namespace LocalScribe.Diarizer;

// Humble object over sherpa-onnx OfflineSpeakerDiarization. No LocalScribe logic here.
// API surface confirmed empirically by the Task 0 spike
// (docs/plans/2026-07-04-stage-5-spike-notes.md, Sections 2, 4.1, 5) -- this
// supersedes the plan brief's sketch, which had two compile-breaking errors
// (ProcessWithCallback's third IntPtr arg, and a non-existent SortByStartTime()).
internal sealed class SherpaDiarisationRunner
{
    /// <summary>Cosine-distance stop threshold for sherpa-onnx FastClustering when no exact count is
    /// forced. HIGHER merges more aggressively -> FEWER clusters. Raised from the spike's 0.5
    /// (2026-07-30) after 0.5 over-split a real 21-min 2-speaker recording into 100+ clusters -
    /// short, noisy CAM++ segment embeddings sit far enough apart at 0.5 to each seed their own
    /// cluster. 0.7 is PROVISIONAL and still needs DER validation on real audio; the deterministic
    /// escape is forcing an exact count (DiarisationJob.ForcedClusterCount / the Split-speakers
    /// "Run with count" input), which bypasses this threshold entirely.</summary>
    private const float AutoClusteringThreshold = 0.7f;

    public DiarisationResultPayload Run(
        float[] samples16kMono,
        string segModelPath,
        string embModelPath,
        int? forcedClusterCount,
        Action<double> onProgress)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segModelPath;
        config.Embedding.Model = embModelPath;
        if (forcedClusterCount is int k && k > 0)
            config.Clustering.NumClusters = k;      // hard forced count
        else
            config.Clustering.Threshold = AutoClusteringThreshold;   // auto (2026-07-30; see const)

        using var sd = new OfflineSpeakerDiarization(config);

        // Progress callback receives processed/total chunk counts; return value is ignored
        // (no cooperative cancel -- confirmed by the spike).
        OfflineSpeakerDiarizationProgressCallback cb = (processed, total, _) =>
        {
            if (total > 0) onProgress(Math.Clamp((double)processed / total, 0, 1));
            return 0;
        };

        var segments = sd.ProcessWithCallback(samples16kMono, cb, IntPtr.Zero)
            .OrderBy(s => s.Start)
            .Select(s => new WireSegment(
                StartMs: (long)Math.Round(s.Start * 1000),
                EndMs: (long)Math.Round(s.End * 1000),
                Cluster: s.Speaker))
            .ToList();

        // Distinct speaker ids present, NOT max+1: sherpa can label a lone region
        // "speaker=1", which would make max+1 over-count and materialise a phantom cluster.
        int clusterCount = segments.Select(s => s.Cluster).Distinct().Count();

        return new DiarisationResultPayload(segments, clusterCount,
            "sherpa-onnx:pyannote-seg-3.0+campplus-zh-en");
    }
}

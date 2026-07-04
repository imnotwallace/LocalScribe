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
            config.Clustering.Threshold = 0.5f;      // auto; threshold pinned in spike

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

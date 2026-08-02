using LocalScribe.Core.Diarisation;
using SherpaOnnx;

namespace LocalScribe.Diarizer;

// Humble object over sherpa-onnx OfflineSpeakerDiarization, now used ONLY to harvest
// near-raw pyannote segment boundaries (in-house clustering design 2026-08-02). The tiny
// clustering threshold stops sherpa's AHC from merging, so its adjacent-same-label segment
// merge almost never fires and the emitted boundaries approximate raw pyannote segmentation;
// the cluster labels themselves are discarded. API surface confirmed by the Task 0 spike
// (docs/plans/2026-07-04-stage-5-spike-notes.md).
internal sealed class SherpaDiarisationRunner
{
    /// <summary>Cosine-distance stop threshold chosen to PREVENT merging (near-every segment
    /// keeps its own label). Only near-identical neighbouring embeddings merge, which is
    /// same-speaker by construction and therefore harmless to boundary quality.</summary>
    private const float HarvestThreshold = 0.05f;

    // MinDurationOn/MinDurationOff stay at sherpa defaults (0.3/0.5) - pinned deliberately:
    // raising MinDurationOn was measured NOT to fix the old clustering collapse, and the
    // in-house clusterer handles short segments itself (bridge attach).

    public IReadOnlyList<EmbedRange> Harvest(
        float[] samples16kMono,
        string segModelPath,
        string embModelPath,
        Action<double> onProgress)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segModelPath;
        config.Embedding.Model = embModelPath;
        config.Clustering.Threshold = HarvestThreshold;

        using var sd = new OfflineSpeakerDiarization(config);

        OfflineSpeakerDiarizationProgressCallback cb = (processed, total, _) =>
        {
            if (total > 0) onProgress(Math.Clamp((double)processed / total, 0, 1));
            return 0;
        };

        return sd.ProcessWithCallback(samples16kMono, cb, IntPtr.Zero)
            .OrderBy(s => s.Start)
            .Select(s => new EmbedRange(
                StartMs: (long)Math.Round(s.Start * 1000),
                EndMs: (long)Math.Round(s.End * 1000)))
            .ToList();
    }
}

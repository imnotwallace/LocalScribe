using System.Text.Json;

namespace LocalScribe.Core.Diarisation;

public sealed class SherpaHelperDiariser(IDiarisationHelper helper) : IDiarisationEngine
{
    public async Task<DiarisationResult> DiariseAsync(
        DiarisationRequest request, IProgress<double> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var job = new DiarisationJob(request.FlacPath, request.Source.ToString(),
            request.SegmentationModelPath, request.EmbeddingModelPath, request.ForcedClusterCount);

        DiarisationResultPayload? result = null;
        DiarisationErrorPayload? error = null;

        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            // Peek at keys to route without exceptions on the hot progress path.
            if (line.Contains("\"progress\""))
            {
                var p = JsonSerializer.Deserialize<DiarisationProgress>(line, DiarisationJson.Options);
                if (p is not null) progress.Report(p.Progress);
            }
            else if (line.Contains("\"error\""))
                error = JsonSerializer.Deserialize<DiarisationErrorPayload>(line, DiarisationJson.Options);
            else if (line.Contains("\"segments\""))
                result = JsonSerializer.Deserialize<DiarisationResultPayload>(line, DiarisationJson.Options);
        }

        int exit = await helper.RunAsync(job, OnLine, ct);   // throws OperationCanceledException on cancel

        if (error is not null)
            throw new DiarisationException(MapError(error.Error), error.Detail ?? error.Error);
        if (exit != 0 || result is null)
            throw new DiarisationException(DiarisationErrorCode.HelperCrash,
                $"diarisation helper exited {exit} without a result");

        var segments = result.Segments
            .Select(s => new DiarisedSegment(s.StartMs, s.EndMs, s.Cluster)).ToList();
        return new DiarisationResult(segments, result.ClusterCount, result.Method);
    }

    private static DiarisationErrorCode MapError(string code) => code switch
    {
        "MODEL_MISSING" => DiarisationErrorCode.ModelDownloadFailed,
        "BAD_AUDIO" => DiarisationErrorCode.BadAudio,
        _ => DiarisationErrorCode.HelperCrash,
    };
}

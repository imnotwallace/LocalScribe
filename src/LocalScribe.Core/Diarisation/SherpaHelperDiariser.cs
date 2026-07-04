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
            // Parsing is best-effort: a malformed line (truncated/garbage JSON from the
            // helper) is ignored rather than fatal, since JsonException is not part of
            // this method's contract. A malformed terminal line then leaves
            // result/error null, so the exit-code/null-result check below still
            // classifies it correctly as HelperCrash. OperationCanceledException is
            // never thrown from JSON parsing, so it is never at risk of being swallowed here.
            try
            {
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
            catch (JsonException)
            {
                // Malformed helper output - ignore this line; terminal null-result/exit
                // checks below classify the overall run as HelperCrash if nothing usable arrived.
            }
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

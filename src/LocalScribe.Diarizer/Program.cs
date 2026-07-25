// Out-of-process diarisation helper. Reads one job JSON object from stdin: a legacy
// DiarisationJob (no "op" property) decodes the retained FLAC leg and runs sherpa-onnx
// offline speaker diarisation, optionally emitting per-cluster mean embeddings when
// emitEmbeddings==true; an EmbedJob ("op":"embed", voiceprint design 2026-07-25) returns
// one mean speaker embedding over explicit time ranges. Streams progress + exactly one
// result-or-error JSON object to stdout.
//
// Stdout contract: zero or more {"progress":<0..1>} lines, then exactly one terminal
// line -- {"segments":[...],"clusterCount":N,"method":"..."} (diarise),
// {"embedding":[...],"method":"..."} (embed), or {"error":"<CODE>","detail":"..."} --
// then exit 0 on success, non-zero on error.
using System.Text.Json;
using LocalScribe.Core.Diarisation;
using LocalScribe.Diarizer;

var stdout = Console.Out;

void Emit(object payload) => stdout.WriteLine(JsonSerializer.Serialize(payload, DiarisationJson.Options));
int Fail(string code, string detail) { Emit(new DiarisationErrorPayload(code, detail)); return 1; }

try
{
    string input = await Console.In.ReadToEndAsync();

    // Op routing (voiceprint design 2026-07-25): "embed" jobs carry op=="embed"; a legacy
    // DiarisationJob has no op property and takes the original path unchanged.
    var probe = System.Text.Json.Nodes.JsonNode.Parse(input)?.AsObject();
    if (probe is not null && probe.TryGetPropertyValue("op", out var opNode) && opNode?.GetValue<string>() == "embed")
    {
        var embedJob = JsonSerializer.Deserialize<EmbedJob>(input, DiarisationJson.Options)
                       ?? throw new InvalidDataException("empty embed job");
        if (!File.Exists(embedJob.EmbeddingModelPath))
            return Fail("MODEL_MISSING", "embedding model file not found");
        float[] embedSamples;
        try { embedSamples = FlacPcmReader.ReadMono16k(embedJob.FlacPath); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        { return Fail("BAD_AUDIO", ex.Message); }

        var sliced = EmbeddingSamples.Slice(embedSamples, embedJob.Ranges);
        if (sliced.Length == 0) return Fail("BAD_AUDIO", "embed ranges cover no audio");
        using var embedder = new SherpaEmbeddingRunner(embedJob.EmbeddingModelPath);
        Emit(new EmbedResultPayload(embedder.Compute(sliced), EmbeddingMethods.CampPlus));
        return 0;
    }

    var job = JsonSerializer.Deserialize<DiarisationJob>(input, DiarisationJson.Options)
              ?? throw new InvalidDataException("empty job");

    if (!File.Exists(job.SegmentationModelPath) || !File.Exists(job.EmbeddingModelPath))
        return Fail("MODEL_MISSING", "segmentation or embedding model file not found");

    float[] samples;
    try { samples = FlacPcmReader.ReadMono16k(job.FlacPath); }
    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
    { return Fail("BAD_AUDIO", ex.Message); }

    var runner = new SherpaDiarisationRunner();
    var result = runner.Run(samples, job.SegmentationModelPath, job.EmbeddingModelPath,
        job.ForcedClusterCount, p => Emit(new DiarisationProgress(p)));

    if (job.EmitEmbeddings)
    {
        using var embedder = new SherpaEmbeddingRunner(job.EmbeddingModelPath);
        var byCluster = new Dictionary<string, float[]>();
        foreach (var group in result.Segments.GroupBy(s => s.Cluster))
        {
            var sliced2 = EmbeddingSamples.Slice(samples,
                group.Select(s => new EmbedRange(s.StartMs, s.EndMs)));
            if (sliced2.Length > 0) byCluster[group.Key.ToString()] = embedder.Compute(sliced2);
        }
        result = result with { ClusterEmbeddings = byCluster, EmbeddingMethod = EmbeddingMethods.CampPlus };
    }
    Emit(result);
    return 0;
}
catch (Exception ex)
{
    return Fail("HELPER_CRASH", ex.Message);
}

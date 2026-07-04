// Out-of-process diarisation helper. Reads one DiarisationJob JSON object from stdin,
// decodes the retained FLAC leg, runs sherpa-onnx offline speaker diarisation, and
// streams progress + exactly one result-or-error JSON object to stdout.
//
// Stdout contract: zero or more {"progress":<0..1>} lines, then exactly one
// {"segments":[...],"clusterCount":N,"method":"..."} result line OR one
// {"error":"<CODE>","detail":"..."} error line. Exit 0 on success, non-zero on error.
using System.Text.Json;
using LocalScribe.Core.Diarisation;
using LocalScribe.Diarizer;

var stdout = Console.Out;

void Emit(object payload) => stdout.WriteLine(JsonSerializer.Serialize(payload, DiarisationJson.Options));
int Fail(string code, string detail) { Emit(new DiarisationErrorPayload(code, detail)); return 1; }

try
{
    string input = await Console.In.ReadToEndAsync();
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
    Emit(result);
    return 0;
}
catch (Exception ex)
{
    return Fail("HELPER_CRASH", ex.Message);
}

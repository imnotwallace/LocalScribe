using System.Text.Json;
using LocalScribe.Core.Diarisation;

public class DiarisationWireTests
{
    [Fact]
    public void Job_round_trips_camelCase()
    {
        var job = new DiarisationJob("C:\\s\\remote.flac", "Remote", "seg.onnx", "emb.onnx", 3);
        string json = JsonSerializer.Serialize(job, DiarisationJson.Options);
        Assert.Contains("\"flacPath\"", json);
        Assert.Contains("\"forcedClusterCount\":3", json);
        var back = JsonSerializer.Deserialize<DiarisationJob>(json, DiarisationJson.Options)!;
        Assert.Equal("Remote", back.Source);
        Assert.Equal(3, back.ForcedClusterCount);
    }

    [Fact]
    public void Result_and_error_payloads_deserialize_from_helper_lines()
    {
        string resultLine = "{\"segments\":[{\"startMs\":0,\"endMs\":1500,\"cluster\":0}],\"clusterCount\":2,\"method\":\"sherpa\"}";
        var r = JsonSerializer.Deserialize<DiarisationResultPayload>(resultLine, DiarisationJson.Options)!;
        Assert.Equal(2, r.ClusterCount);
        Assert.Single(r.Segments);
        Assert.Equal(1500, r.Segments[0].EndMs);

        string errLine = "{\"error\":\"MODEL_MISSING\",\"detail\":\"no file\"}";
        var e = JsonSerializer.Deserialize<DiarisationErrorPayload>(errLine, DiarisationJson.Options)!;
        Assert.Equal("MODEL_MISSING", e.Error);
    }

    [Fact]
    public void Job_without_emitEmbeddings_deserializes_false()
    {
        var job = JsonSerializer.Deserialize<DiarisationJob>(
            "{\"flacPath\":\"a.flac\",\"source\":\"Remote\",\"segmentationModelPath\":\"s\",\"embeddingModelPath\":\"e\",\"forcedClusterCount\":null}",
            DiarisationJson.Options)!;
        Assert.False(job.EmitEmbeddings);
    }

    [Fact]
    public void Result_without_clusterEmbeddings_deserializes_null()
    {
        var r = JsonSerializer.Deserialize<DiarisationResultPayload>(
            "{\"segments\":[],\"clusterCount\":0,\"method\":\"m\"}", DiarisationJson.Options)!;
        Assert.Null(r.ClusterEmbeddings);
        Assert.Null(r.EmbeddingMethod);
    }

    [Fact]
    public void Result_with_clusterEmbeddings_round_trips()
    {
        var payload = new DiarisationResultPayload([], 1, "m",
            new Dictionary<string, float[]> { ["0"] = [0.1f, 0.2f] }, EmbeddingMethods.CampPlus);
        var json = JsonSerializer.Serialize(payload, DiarisationJson.Options);
        var back = JsonSerializer.Deserialize<DiarisationResultPayload>(json, DiarisationJson.Options)!;
        Assert.Equal(0.2f, back.ClusterEmbeddings!["0"][1]);
        Assert.Equal("campplus-zh-en", back.EmbeddingMethod);
    }

    [Fact]
    public void EmbedJob_and_result_round_trip()
    {
        var job = new EmbedJob("embed", "a.flac", [new EmbedRange(0, 1500)], "e.onnx");
        var back = JsonSerializer.Deserialize<EmbedJob>(
            JsonSerializer.Serialize(job, DiarisationJson.Options), DiarisationJson.Options)!;
        Assert.Equal("embed", back.Op);
        Assert.Equal(1500, back.Ranges[0].EndMs);
        var res = JsonSerializer.Deserialize<EmbedResultPayload>(
            "{\"embedding\":[1.0,2.0],\"method\":\"campplus-zh-en\"}", DiarisationJson.Options)!;
        Assert.Equal(2f, res.Embedding[1]);
    }
}

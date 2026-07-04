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
}

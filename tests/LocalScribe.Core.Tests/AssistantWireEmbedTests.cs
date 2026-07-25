using LocalScribe.Core.Assistant;

public sealed class AssistantWireEmbedTests
{
    [Fact]
    public void EmbedResult_round_trips_vectors_and_method()
    {
        var evt = new AssistantEmbedResult(
            [[0.25f, -0.5f, 1f], [0f, 0.125f, -1f]], "embeddinggemma-300m-q8_0@256");
        string line = AssistantWire.SerializeEvent(evt);
        var parsed = Assert.IsType<AssistantEmbedResult>(AssistantWire.ParseEventLine(line));
        Assert.Equal("embeddinggemma-300m-q8_0@256", parsed.Method);
        Assert.Equal(2, parsed.Embeddings.Count);
        Assert.Equal(new[] { 0.25f, -0.5f, 1f }, parsed.Embeddings[0]);
        Assert.Equal(new[] { 0f, 0.125f, -1f }, parsed.Embeddings[1]);
    }

    [Fact]
    public void EmbedResult_line_is_single_line_json()
    {
        string line = AssistantWire.SerializeEvent(new AssistantEmbedResult([[1f]], "m@1"));
        Assert.DoesNotContain('\n', line);
        Assert.StartsWith("{", line);
    }

    [Fact]
    public void EmbedPayload_carries_kind_dim_and_texts()
    {
        string payload = AssistantWire.EmbedPayload("query", ["a", "b"], 256);
        var o = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        Assert.Equal("query", o["kind"]!.GetValue<string>());
        Assert.Equal(256, o["dim"]!.GetValue<int>());
        Assert.Equal(2, o["texts"]!.AsArray().Count);
        Assert.Equal("a", o["texts"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Malformed_embedResult_parses_null_never_throws()
    {
        // top-level embeddings not an array
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"embedResult\",\"embeddings\":\"junk\"}"));
        // non-numeric leaf inside a nested vector
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"embedResult\",\"embeddings\":[[1,\"x\",3]]}"));
        // a row that is not an array
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"embedResult\",\"embeddings\":[42]}"));
        // unknown type still null (existing rule)
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"wat\"}"));
        // empty array is valid
        var empty = Assert.IsType<AssistantEmbedResult>(AssistantWire.ParseEventLine("{\"type\":\"embedResult\",\"method\":\"m\",\"embeddings\":[]}"));
        Assert.Empty(empty.Embeddings);
    }
}

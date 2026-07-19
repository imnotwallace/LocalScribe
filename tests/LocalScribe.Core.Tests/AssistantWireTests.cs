using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantWireTests
{
    [Fact]
    public void Request_serializes_to_the_locked_single_line_wire_shape_and_round_trips()
    {
        // Design 2026-07-18 section 7.1: the wire is a LOCKED contract feat/matter-qa consumes.
        var req = new AssistantRequest("summarize", @"C:\models\q.gguf", 16384, "auto", false,
            "{\"prompt\":\"hi\",\"maxTokens\":600}");
        string line = AssistantWire.SerializeRequest(req);

        Assert.DoesNotContain('\n', line);                       // JSON-LINES: one request, one line
        Assert.Contains("\"op\":\"summarize\"", line);
        Assert.Contains("\"ctxTokens\":16384", line);
        Assert.Contains("\"kvQuant\":\"q8_0\"", line);           // constant on the wire (design 7.2)
        Assert.Contains("\"backend\":\"auto\"", line);
        Assert.Contains("\"keepAlive\":false", line);
        Assert.Contains("\"payload\":{\"prompt\":\"hi\",\"maxTokens\":600}", line);

        var back = AssistantWire.ParseRequestLine(line);
        Assert.NotNull(back);
        Assert.Equal(req.Op, back!.Op);
        Assert.Equal(req.ModelPath, back.ModelPath);
        Assert.Equal(req.CtxTokens, back.CtxTokens);
        Assert.Equal(req.Backend, back.Backend);
        Assert.Equal(req.KeepAlive, back.KeepAlive);
        Assert.Equal(req.PayloadJson, back.PayloadJson);
    }

    [Fact]
    public void Events_round_trip_through_both_codecs()
    {
        AssistantEvent[] events =
        [
            new AssistantChunk("Hello "),
            new AssistantProgress("map", 2, 5),
            new AssistantDone("cuda", 1234, 210),
            new AssistantError("JOB_FAILED: boom"),
        ];
        foreach (var evt in events)
        {
            string line = AssistantWire.SerializeEvent(evt);
            Assert.DoesNotContain('\n', line);
            Assert.Equal(evt, AssistantWire.ParseEventLine(line));   // record value equality
        }
        // The done line carries the LOCKED nested stats shape.
        Assert.Contains("\"stats\":{\"backend\":\"cuda\",\"promptTokens\":1234,\"outputTokens\":210}",
            AssistantWire.SerializeEvent(new AssistantDone("cuda", 1234, 210)));
    }

    [Fact]
    public void Malformed_or_unknown_lines_parse_to_null_never_throw()
    {
        // SherpaHelperDiariser precedent: non-protocol stdout noise is skipped, never fatal.
        Assert.Null(AssistantWire.ParseEventLine("not json at all"));
        Assert.Null(AssistantWire.ParseEventLine("42"));
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"mystery\"}"));
        Assert.Null(AssistantWire.ParseEventLine("{}"));
        Assert.Null(AssistantWire.ParseRequestLine("not json at all"));
        Assert.Null(AssistantWire.ParseRequestLine("{\"modelPath\":\"x\"}"));   // no op -> reject
        Assert.Null(AssistantWire.ParseRequestLine("{\"op\":\"summarize\"}"));  // no modelPath -> reject
    }

    [Fact]
    public void SerializeRequest_falls_back_to_an_empty_object_on_malformed_payload_json()
    {
        // ParseOrEmpty (AssistantWire.cs, private helper): a malformed PayloadJson must never
        // throw or corrupt the wire line - it silently becomes an empty object under "payload".
        foreach (var malformed in new[] { "not json{", "{{oops" })
        {
            var req = new AssistantRequest("summarize", @"C:\models\q.gguf", 16384, "auto", false, malformed);

            string? line = null;
            var ex = Record.Exception(() => line = AssistantWire.SerializeRequest(req));

            Assert.Null(ex);                          // never throws on bad caller input
            Assert.Contains("\"payload\":{}", line);   // falls back to {}, line still valid JSON
        }
    }

    [Fact]
    public void PromptPayload_builds_the_v1_payload_shape()
    {
        Assert.Equal("{\"prompt\":\"do it\",\"maxTokens\":600}", AssistantWire.PromptPayload("do it", 600));
    }
}

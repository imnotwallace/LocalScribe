using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LocalScribe.Core.Assistant;
using LocalScribe.Inference;

namespace LocalScribe.Core.Tests;

/// <summary>LocalScribe.Inference.exe's request loop (design 2026-10-18), driven through its stdio
/// contract against a scripted HTTP server: what goes over the wire to the endpoint, and what comes
/// back to the App. Requests are written with CORE's serializer and every event the helper emits is
/// read back with CORE's parser - the helper keeps its own hand-written copy of the wire (it does
/// not reference Core), so this is also the test that the two stay compatible.</summary>
public class InferenceHelperTests
{
    /// <summary>Records each request (body read eagerly - the helper disposes it) and replies with
    /// the next scripted response.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return Responses.Count > 0 ? Responses.Dequeue()() : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string body, string mediaType)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static readonly RemoteEndpointConfig Endpoint = new("https://inference.example:8443", "served-model", "k-123");

    private static string Line(string op, string payloadJson, bool keepAlive = false, RemoteEndpointConfig? remote = null)
        => AssistantWire.SerializeRequest(new AssistantRequest(op, "", 0, AssistantWire.RemoteBackend, keepAlive,
            payloadJson, remote ?? Endpoint));

    private static async Task<(int Exit, List<AssistantEvent> Events)> Run(FakeServer server, params string[] lines)
    {
        var events = new List<AssistantEvent>();
        using var http = RemoteHttp.CreateClient(server);
        // Exactly what the App sees: the helper's line, parsed by Core's codec. Null = a line Core
        // would skip as noise - which would be a contract break, so it fails the test.
        var helper = new InferenceHelper(http,
            e => events.Add(AssistantWire.ParseEventLine(HelperWire.Serialize(e))
                            ?? throw new Xunit.Sdk.XunitException("Core cannot parse: " + HelperWire.Serialize(e))),
            retryBackoff: _ => TimeSpan.Zero);
        int exit = await helper.RunAsync(new StringReader(string.Join("\n", lines)), default);
        return (exit, events);
    }

    // ---------- chat ----------

    [Fact]
    public async Task Chat_streams_the_answer_and_reports_usage_and_the_served_model()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, """
            : keep-alive comment
            data: {"model":"served-model-q4","choices":[{"delta":{"role":"assistant"}}]}

            data: {"choices":[{"delta":{"content":"Hello"}}]}

            data: {"choices":[{"delta":{"content":" world"}}]}

            data: {"choices":[],"usage":{"prompt_tokens":42,"completion_tokens":2}}

            data: [DONE]

            """, "text/event-stream"));

        var (exit, events) = await Run(server, Line("summarize", AssistantWire.PromptPayload("Summarize this.", 600)));

        Assert.Equal(0, exit);
        Assert.Equal(["Hello", " world"], events.OfType<AssistantChunk>().Select(c => c.Text));
        Assert.Equal(new AssistantDone("remote", 42, 2, "served-model-q4"), events[^1]);
    }

    [Fact]
    public async Task Chat_sends_the_prompt_as_one_plain_user_message_with_bearer_auth()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, "data: [DONE]\n", "text/event-stream"));

        await Run(server, Line("answer", AssistantWire.PromptPayload("<|im_start|> is the App's business, not ours", 1024)));

        var (request, body) = Assert.Single(server.Seen);
        Assert.Equal("https://inference.example:8443/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("k-123", request.Headers.Authorization.Parameter);
        var json = JsonNode.Parse(body)!;
        var message = Assert.Single(json["messages"]!.AsArray())!;
        Assert.Equal("user", message["role"]!.GetValue<string>());
        // Byte-identical: no chat-template tokens added - the server applies its own template.
        Assert.Equal("<|im_start|> is the App's business, not ours", message["content"]!.GetValue<string>());
        Assert.Equal("served-model", json["model"]!.GetValue<string>());
        Assert.Equal(1024, json["max_tokens"]!.GetValue<int>());
        Assert.True(json["stream"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("https://inference.example:8443")]
    [InlineData("https://inference.example:8443/")]
    [InlineData("https://inference.example:8443/v1")]
    [InlineData("https://inference.example:8443/V1/")]
    public async Task A_base_url_with_or_without_v1_reaches_the_same_route(string endpoint)
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, "data: [DONE]\n", "text/event-stream"));

        await Run(server, Line("answer", AssistantWire.PromptPayload("q", 8), remote: Endpoint with { Endpoint = endpoint }));

        Assert.Equal("https://inference.example:8443/v1/chat/completions", server.Seen[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Chat_without_a_model_lets_the_server_choose_and_records_what_it_says_it_used()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK,
            """{"model":"default-model","choices":[{"message":{"content":"Whole answer."}}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""",
            "application/json"));

        var (_, events) = await Run(server,
            Line("answer", AssistantWire.PromptPayload("q", 64), remote: Endpoint with { Model = "", ApiKey = null }));

        Assert.False(JsonNode.Parse(server.Seen[0].Body)!.AsObject().ContainsKey("model"));
        Assert.Null(server.Seen[0].Request.Headers.Authorization);
        Assert.Equal("Whole answer.", Assert.Single(events.OfType<AssistantChunk>()).Text);
        Assert.Equal(new AssistantDone("remote", 5, 3, "default-model"), events[^1]);
    }

    [Fact]
    public async Task A_refusal_is_reported_with_the_status_and_never_echoes_the_key()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.Unauthorized, """{"error":"bad key"}""", "application/json"));

        var (exit, events) = await Run(server, Line("summarize", AssistantWire.PromptPayload("p", 10)));

        Assert.Equal(1, exit);
        var error = Assert.IsType<AssistantError>(Assert.Single(events, e => e is not AssistantProgress));
        Assert.StartsWith("REMOTE_FAILED: https://inference.example:8443 answered HTTP 401", error.Message);
        Assert.Contains("bad key", error.Message);
        Assert.DoesNotContain("k-123", error.Message);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_says_so()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://elsewhere.example/");
            return r;
        });

        var (_, events) = await Run(server, Line("summarize", AssistantWire.PromptPayload("p", 10)));

        Assert.Single(server.Seen);
        Assert.Contains("redirects are not followed", events.OfType<AssistantError>().Single().Message);
    }

    [Fact]
    public async Task An_error_part_way_through_the_stream_fails_the_job()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK,
            "data: {\"choices\":[{\"delta\":{\"content\":\"Par\"}}]}\n\ndata: {\"error\":{\"message\":\"context overflow\"}}\n\n",
            "text/event-stream"));

        var (exit, events) = await Run(server, Line("summarize", AssistantWire.PromptPayload("p", 10)));

        Assert.Equal(1, exit);
        Assert.Contains("context overflow", events.OfType<AssistantError>().Single().Message);
        Assert.DoesNotContain(events, e => e is AssistantDone);
    }

    // ---------- transcription ----------

    private static string TranscribeLine(bool keepAlive = true, string? language = "en", string? prompt = "Acme")
        => Line(AssistantWire.TranscribeOp,
            AssistantWire.TranscribePayload(Convert.ToBase64String([1, 2, 3, 4]), language, prompt), keepAlive);

    [Fact]
    public async Task Transcribe_uploads_multipart_and_normalizes_the_servers_language_name()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, """
            {"text":" Hello there. ","language":"english",
             "segments":[{"no_speech_prob":0.4},{"no_speech_prob":0.02}]}
            """, "application/json"));

        var (exit, events) = await Run(server, TranscribeLine(keepAlive: false));

        Assert.Equal(0, exit);
        Assert.Equal(new AssistantTranscription("Hello there.", "en", 0.02), events.OfType<AssistantTranscription>().Single());
        Assert.Equal(new AssistantDone("remote", 0, 0, "served-model"), events[^1]);

        var (request, body) = Assert.Single(server.Seen);
        Assert.Equal("https://inference.example:8443/v1/audio/transcriptions", request.RequestUri!.ToString());
        Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("name=file; filename=segment.wav", body);
        foreach (string part in new[] { "name=model", "served-model", "name=response_format", "verbose_json",
                     "name=language", "name=prompt", "Acme" })
            Assert.Contains(part, body);
    }

    [Fact]
    public async Task Transcribe_omits_language_and_prompt_when_there_are_none()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, """{"text":"x"}""", "application/json"));

        var (_, events) = await Run(server, TranscribeLine(keepAlive: false, language: null, prompt: null));

        Assert.DoesNotContain("name=language", server.Seen[0].Body);
        Assert.DoesNotContain("name=prompt", server.Seen[0].Body);
        Assert.Equal(new AssistantTranscription("x", null, null), events.OfType<AssistantTranscription>().Single());
    }

    [Fact]
    public async Task Transcribe_retries_a_transient_failure_and_tells_the_app_it_is_still_alive()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.ServiceUnavailable, "busy", "text/plain"));
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, """{"text":"ok","language":"en"}""", "application/json"));

        var (exit, events) = await Run(server, TranscribeLine(keepAlive: false));

        Assert.Equal(0, exit);
        Assert.Equal(2, server.Seen.Count);
        Assert.Contains(events, e => e is AssistantProgress { Phase: "remote-retry", Current: 1 });
        Assert.Equal("ok", events.OfType<AssistantTranscription>().Single().Text);
    }

    [Fact]
    public async Task Transcribe_does_not_retry_a_refusal()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.BadRequest, "unknown model", "text/plain"));

        var (exit, events) = await Run(server, TranscribeLine());

        Assert.Equal(1, exit);
        Assert.Single(server.Seen);
        Assert.Contains("unknown model", events.OfType<AssistantError>().Single().Message);
    }

    [Fact]
    public async Task Transcribe_gives_up_after_the_last_transient_attempt()
    {
        var server = new FakeServer();
        for (int i = 0; i < 5; i++)
            server.Responses.Enqueue(() => Respond(HttpStatusCode.BadGateway, "", "text/plain"));

        var (exit, _) = await Run(server, TranscribeLine());

        Assert.Equal(1, exit);
        Assert.Equal(TranscriptionClient.MaxAttempts, server.Seen.Count);
    }

    [Fact]
    public async Task KeepAlive_serves_one_segment_per_line_on_the_same_process()
    {
        var server = new FakeServer();
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, """{"text":"one"}""", "application/json"));
        server.Responses.Enqueue(() => Respond(HttpStatusCode.OK, """{"text":"two"}""", "application/json"));

        var (exit, events) = await Run(server, TranscribeLine(), TranscribeLine());

        Assert.Equal(0, exit);   // stdin EOF after two requests
        Assert.Equal(["one", "two"], events.OfType<AssistantTranscription>().Select(t => t.Text));
    }

    // ---------- contract ----------

    [Fact]
    public async Task A_request_without_a_remote_block_is_refused_and_nothing_is_sent()
    {
        var server = new FakeServer();
        string local = AssistantWire.SerializeRequest(new AssistantRequest("summarize", "m.gguf", 0, "auto", false,
            AssistantWire.PromptPayload("p", 1)));

        var (exit, events) = await Run(server, local);

        Assert.Equal(1, exit);
        Assert.StartsWith("BAD_REQUEST", Assert.IsType<AssistantError>(Assert.Single(events)).Message);
        Assert.Empty(server.Seen);
    }

    [Fact]
    public async Task Embedding_jobs_are_not_the_remote_helpers_to_run()
    {
        var server = new FakeServer();
        var (exit, events) = await Run(server, Line("embed", "{}"));

        Assert.Equal(1, exit);
        Assert.Contains("does not run 'embed'", events.OfType<AssistantError>().Single().Message);
        Assert.Empty(server.Seen);
    }

    [Fact]
    public void The_hand_written_wire_matches_Core()
    {
        Assert.Equal(AssistantWire.RemoteBackend, HelperWire.RemoteBackend);
        Assert.Equal(AssistantWire.TranscribeOp, HelperWire.TranscribeOp);

        var parsed = HelperWire.ParseRequest(Line(AssistantWire.TranscribeOp, "{\"a\":1}", keepAlive: true))!;
        Assert.Equal(new HelperRequest(AssistantWire.TranscribeOp, true, "{\"a\":1}",
            new RemoteEndpoint(Endpoint.Endpoint, Endpoint.Model, Endpoint.ApiKey)), parsed);
        Assert.DoesNotContain("k-123", parsed.ToString());
        Assert.Equal("https://inference.example:8443", parsed.Remote!.Origin);

        Assert.Equal(new AssistantDone("remote", 1, 2, "m"), AssistantWire.ParseEventLine(HelperWire.Serialize(new Done(1, 2, "m"))));
        Assert.Equal(new AssistantTranscription("t", null, null),
            AssistantWire.ParseEventLine(HelperWire.Serialize(new LocalScribe.Inference.Transcription("t", null, null))));
        Assert.Equal(new AssistantProgress("remote", 0, 0), AssistantWire.ParseEventLine(HelperWire.Serialize(new Progress("remote", 0, 0))));
        Assert.Equal(new AssistantChunk("c"), AssistantWire.ParseEventLine(HelperWire.Serialize(new Chunk("c"))));
        Assert.Equal(new AssistantError("e"), AssistantWire.ParseEventLine(HelperWire.Serialize(new Failure("e"))));
    }

    [Theory]
    [InlineData("english", "en")]
    [InlineData("EN", "en")]
    [InlineData("haitian creole", "ht")]
    [InlineData("yue", "yue")]
    [InlineData("klingon", null)]
    [InlineData(null, null)]
    public void Server_language_names_normalize_to_whisper_codes(string? reported, string? code)
        => Assert.Equal(code, WhisperLanguages.ToCode(reported));
}

using System.Text.Json.Nodes;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Tests;

/// <summary>Opt-in remote inference, App/Core side (design 2026-10-18): settings resolve to a route
/// or a named problem, the wire carries the endpoint, and the routing runner sends a job to the
/// remote helper only when the user turned it on - never silently to the other side.</summary>
public class RemoteInferenceRoutingTests
{
    private static Settings With(RemoteInferenceSetting assistant, RemoteInferenceSetting? transcription = null)
        => new() { Inference = new InferenceSetting { Assistant = assistant, Transcription = transcription ?? new() } };

    private static RemoteInferenceSetting Remote(string endpoint = "https://inference.example:8443/", string model = "m")
        => new() { Backend = RemoteInferenceSetting.Remote, Endpoint = endpoint, Model = model };

    private static async Task<List<AssistantEvent>> Collect(IAsyncEnumerable<AssistantEvent> stream)
    {
        var list = new List<AssistantEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    private static AssistantRequest Summarize()
        => new("summarize", @"C:\models\q.gguf", 8192, "auto", false, "{\"prompt\":\"p\",\"maxTokens\":600}");

    // ---------- settings -> route ----------

    [Fact]
    public void Default_settings_route_nothing_remotely()
    {
        Assert.Null(RemoteInference.AssistantRoute(new Settings()));
        Assert.Null(RemoteInference.TranscriptionRoute(new Settings(), null));
    }

    [Fact]
    public void Remote_without_an_endpoint_is_not_a_route()
    {
        var s = With(new RemoteInferenceSetting { Backend = RemoteInferenceSetting.Remote, Endpoint = "  " });
        Assert.Null(RemoteInference.AssistantRoute(s));
    }

    [Fact]
    public void Assistant_route_carries_the_trimmed_endpoint_and_model_and_no_key_when_none_is_saved()
    {
        var route = RemoteInference.AssistantRoute(With(Remote()))!;
        Assert.Null(route.Problem);
        Assert.Equal(new RemoteEndpointConfig("https://inference.example:8443", "m", null), route.Config);
    }

    [Fact]
    public void An_unusable_endpoint_is_a_named_problem_not_a_route()
    {
        var route = RemoteInference.AssistantRoute(With(Remote("ftp://inference.example")))!;
        Assert.Null(route.Config);
        Assert.Contains("http://", route.Problem);
    }

    [Fact]
    public void Transcription_route_requires_a_model_and_lets_the_dialog_override_it()
    {
        var noModel = With(new(), Remote(model: ""));
        Assert.Contains("model name", RemoteInference.TranscriptionRoute(noModel, null)!.Problem);
        Assert.Equal("large-v3", RemoteInference.TranscriptionRoute(noModel, " large-v3 ")!.Config!.Model);

        var configured = With(new(), Remote(model: "small"));
        Assert.Equal("small", RemoteInference.TranscriptionRoute(configured, null)!.Config!.Model);
        Assert.Equal("medium", RemoteInference.TranscriptionRoute(configured, "medium")!.Config!.Model);
    }

    [Theory]
    [InlineData("https://inference.example", null)]
    [InlineData("http://192.0.2.10:8080/v1-proxy", null)]
    [InlineData("inference.example:8080", "http")]
    [InlineData("file:///C:/x", "http")]
    [InlineData("https://user:secret@inference.example", "API key")]
    [InlineData("https://inference.example/?key=secret", "no ? or #")]
    public void Endpoint_validation(string endpoint, string? problemContains)
    {
        string? problem = RemoteInference.ValidateEndpoint(endpoint);
        if (problemContains is null) Assert.Null(problem);
        else Assert.Contains(problemContains, problem);
    }

    [Fact]
    public void Origin_drops_the_path()
        => Assert.Equal("https://inference.example:8443", RemoteInference.Origin("https://inference.example:8443/a/b"));

    [Fact]
    public void Helper_is_found_through_the_env_override_then_beside_the_app()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        string env = Path.Combine(dir, "env");
        Directory.CreateDirectory(env);
        try
        {
            Assert.Null(RemoteInference.FindHelperExe(dir, env));
            File.WriteAllText(Path.Combine(dir, RemoteInference.HelperExeName), "");
            Assert.Equal(Path.Combine(dir, RemoteInference.HelperExeName), RemoteInference.FindHelperExe(dir, env));
            File.WriteAllText(Path.Combine(env, RemoteInference.HelperExeName), "");
            Assert.Equal(Path.GetFullPath(Path.Combine(env, RemoteInference.HelperExeName)),
                RemoteInference.FindHelperExe(dir, env));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- wire ----------

    [Fact]
    public void Remote_block_round_trips_and_a_local_request_carries_none()
    {
        var remote = Summarize() with
        {
            Backend = AssistantWire.RemoteBackend,
            Remote = new RemoteEndpointConfig("https://inference.example", "m", "k-123"),
        };
        Assert.Equal(remote, AssistantWire.ParseRequestLine(AssistantWire.SerializeRequest(remote)));

        var local = JsonNode.Parse(AssistantWire.SerializeRequest(Summarize()))!.AsObject();
        Assert.False(local.ContainsKey("remote"));
        Assert.Null(AssistantWire.ParseRequestLine(local.ToJsonString())!.Remote);
    }

    [Fact]
    public void A_logged_request_never_shows_the_api_key()
    {
        var request = Summarize() with { Remote = new RemoteEndpointConfig("https://inference.example", "m", "k-123") };
        Assert.DoesNotContain("k-123", request.ToString());
        Assert.Contains("<redacted>", request.ToString());
    }

    [Fact]
    public void Done_model_round_trips_and_a_local_done_keeps_its_old_shape()
    {
        var remote = new AssistantDone("remote", 10, 20, "served-model");
        Assert.Equal(remote, AssistantWire.ParseEventLine(AssistantWire.SerializeEvent(remote)));

        string local = AssistantWire.SerializeEvent(new AssistantDone("cuda", 10, 20));
        Assert.DoesNotContain("model", local);
        Assert.Equal(new AssistantDone("cuda", 10, 20), AssistantWire.ParseEventLine(local));
    }

    [Fact]
    public void Transcription_event_round_trips_with_and_without_signals()
    {
        foreach (var t in new[] { new AssistantTranscription("hi there", "en", 0.1), new AssistantTranscription("", null, null) })
            Assert.Equal(t, AssistantWire.ParseEventLine(AssistantWire.SerializeEvent(t)));
    }

    // ---------- routing runner ----------

    [Fact]
    public async Task Local_settings_run_the_job_on_the_local_helper_unchanged()
    {
        var local = new FakeAssistantJobRunner();
        var remote = new FakeAssistantJobRunner();
        await Collect(new RemoteRoutingJobRunner(local, remote, () => new Settings()).RunAsync(Summarize(), default));

        Assert.Equal(Summarize(), Assert.Single(local.Requests));
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task Remote_settings_stamp_the_endpoint_and_run_on_the_remote_helper()
    {
        var local = new FakeAssistantJobRunner();
        var remote = new FakeAssistantJobRunner();
        remote.Scripts.Enqueue([new AssistantChunk("x"), new AssistantDone("remote", 1, 1, "m")]);

        var events = await Collect(new RemoteRoutingJobRunner(local, remote, () => With(Remote()))
            .RunAsync(Summarize(), default));

        var sent = Assert.Single(remote.Requests);
        Assert.Equal(AssistantWire.RemoteBackend, sent.Backend);
        Assert.Equal("https://inference.example:8443", sent.Remote!.Endpoint);
        Assert.Equal(Summarize().PayloadJson, sent.PayloadJson);
        Assert.Empty(local.Requests);
        Assert.IsType<AssistantDone>(events[^1]);
    }

    [Fact]
    public async Task Settings_are_read_per_job_so_a_change_applies_to_the_next_one()
    {
        var local = new FakeAssistantJobRunner();
        var remote = new FakeAssistantJobRunner();
        var settings = new Settings();
        var runner = new RemoteRoutingJobRunner(local, remote, () => settings);

        await Collect(runner.RunAsync(Summarize(), default));
        settings = With(Remote());
        await Collect(runner.RunAsync(Summarize(), default));

        Assert.Single(local.Requests);
        Assert.Single(remote.Requests);
    }

    [Fact]
    public async Task Embeddings_always_stay_local()
    {
        var local = new FakeAssistantJobRunner();
        var remote = new FakeAssistantJobRunner();
        var embed = Summarize() with { Op = "embed" };
        await Collect(new RemoteRoutingJobRunner(local, remote, () => With(Remote())).RunAsync(embed, default));

        Assert.Single(local.Requests);
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task An_unusable_route_fails_the_job_and_sends_it_nowhere()
    {
        var local = new FakeAssistantJobRunner();
        var remote = new FakeAssistantJobRunner();
        var events = await Collect(new RemoteRoutingJobRunner(local, remote, () => With(Remote("not a url")))
            .RunAsync(Summarize(), default));

        Assert.IsType<AssistantError>(Assert.Single(events));
        Assert.Empty(local.Requests);
        Assert.Empty(remote.Requests);
    }

    [Fact]
    public async Task A_missing_helper_fails_the_job_instead_of_falling_back_to_local()
    {
        var local = new FakeAssistantJobRunner();
        var events = await Collect(new RemoteRoutingJobRunner(local, remote: null, () => With(Remote()))
            .RunAsync(Summarize(), default));

        var error = Assert.IsType<AssistantError>(Assert.Single(events));
        Assert.Equal(RemoteInference.MissingHelperMessage, error.Message);
        Assert.Empty(local.Requests);
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalScribe.Inference;

/// <summary>OpenAI-compatible POST /v1/chat/completions, streamed (llama.cpp server, vLLM, Ollama,
/// LM Studio and OpenAI itself all speak it). The App builds the COMPLETE prompt - instructions,
/// context and question - exactly as it does for the local model, and it is sent as ONE user
/// message. The server applies its own model's chat template; adding template tokens here would
/// double-wrap the prompt for every server that does (all of them).
///
/// No retry: a chat answer streams to the user as it arrives, so a failure part-way is reported,
/// never silently restarted.</summary>
internal sealed class ChatCompletionClient(HttpClient http, RemoteEndpoint remote)
{
    /// <summary>From the server's usage report, when it sends one (stream_options.include_usage).</summary>
    public int? PromptTokens { get; private set; }
    public int? CompletionTokens { get; private set; }
    /// <summary>The model the server says answered - provenance. Null if it never said.</summary>
    public string? ServedModel { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (remote.Model.Length > 0) body["model"] = remote.Model;

        using var request = new HttpRequestMessage(HttpMethod.Post, RemoteHttp.Url(remote, "/v1/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        RemoteHttp.Authorize(request, remote);
        using HttpResponseMessage response = await SendAsync(request, ct);
        await RemoteHttp.EnsureSuccessAsync(response, remote, ct);

        // A server that ignores "stream" answers with one plain completion object.
        if (response.Content.Headers.ContentType?.MediaType is "application/json")
        {
            JsonNode? whole = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            Observe(whole);
            string? text = HelperWire.Str(FirstChoice(whole)?["message"]?["content"]);
            if (!string.IsNullOrEmpty(text)) yield return text;
            yield break;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await ReadLineAsync(reader, ct) is string line)
        {
            // Server-sent events: only "data:" lines carry payload; comments, event names and the
            // blank separators are skipped.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }   // a malformed keep-alive must not end the answer
            if (chunk?["error"] is JsonNode error)
                throw new RemoteInferenceException(
                    $"{remote.Origin} reported an error part-way through the answer: "
                    + (HelperWire.Str(error["message"]) ?? error.ToJsonString()));
            Observe(chunk);
            string? delta = HelperWire.Str(FirstChoice(chunk)?["delta"]?["content"]);
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    /// <summary>choices[0], or null - the final usage chunk carries an EMPTY choices array, which
    /// the JsonArray indexer would throw on.</summary>
    private static JsonNode? FirstChoice(JsonNode? node)
        => node?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;

    private void Observe(JsonNode? node)
    {
        ServedModel ??= HelperWire.Str(node?["model"]);
        if (node?["usage"] is JsonObject usage)
        {
            PromptTokens = HelperWire.Int(usage["prompt_tokens"]) ?? PromptTokens;
            CompletionTokens = HelperWire.Int(usage["completion_tokens"]) ?? CompletionTokens;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RemoteHttp.RequestTimeout);
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw RemoteHttp.TimedOut(remote); }
        catch (HttpRequestException ex) { throw RemoteHttp.Unreachable(remote, ex); }
    }

    /// <summary>Idle limit per line, not per answer: a long answer that keeps streaming is fine; a
    /// server that goes silent mid-answer is not.</summary>
    private async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(RemoteHttp.RequestTimeout);
        try { return await reader.ReadLineAsync(idle.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw RemoteHttp.TimedOut(remote); }
        catch (IOException ex) { throw RemoteHttp.Unreachable(remote, ex); }
    }
}

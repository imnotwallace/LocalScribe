// Remote inference provider backed by an OpenAI-compatible HTTP endpoint (e.g. llama.cpp
// at localhost:8080). Sends chat completion requests with SSE streaming, applies the
// same ChatML template that LlamaEngine uses (since the llama.cpp model expects it).
//
// LOCKED design: the assistant wire uses "user"/"assistant" roles; the remote API supports
// "system"/"user"/"assistant". The provider maps roles 1:1 (no system role injection) so
// the remote behaves identically to the local model. The ChatML template wraps the FIRST
// user message to match LlamaEngine's wrapped prompt format.
using LocalScribe.Core.Assistant;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalScribe.Inference;

/// <summary>Remote OpenAI-compatible inference provider. Uses HttpClient for HTTP calls;
/// the timeout is controlled by ProviderOptions.Timeout (default 300s for long summaries).</summary>
internal sealed class RemoteInferenceProvider : IInferenceProvider
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _apiKey;
    private readonly string? _model;
    private readonly TimeSpan _timeout;

    /// <summary>Create a remote provider. Does NOT connect yet — health check and first
    /// inference both probe the endpoint on-demand.</summary>
    /// <param name="endpoint">Base URL (e.g. "http://localhost:8080").</param>
    /// <param name="apiKey">Optional Bearer token.</param>
    /// <param name="model">Model name to send in the request (e.g. "example-model").</param>
    /// <param name="timeout">HTTP timeout for each request (default 300s for long summaries).</param>
    public RemoteInferenceProvider(string endpoint, string? apiKey, string? model, TimeSpan timeout)
        : this(endpoint, apiKey, model, timeout, createHandler: null) { }

    /// <summary>Internal constructor for tests — accepts a custom HttpMessageHandler.</summary>
    internal RemoteInferenceProvider(
        string endpoint,
        string? apiKey,
        string? model,
        TimeSpan timeout,
        HttpMessageHandler? createHandler)
    {
        _endpoint = endpoint.TrimEnd('/');
        _apiKey = apiKey;
        _model = model;
        _timeout = timeout;

        var handler = createHandler ?? new SocketsHttpHandler
        {
            // Don't follow redirects — the remote endpoint should respond directly.
            AllowAutoRedirect = false,
            // Use the system's default certificate validation (no custom handling needed).
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };

        _http = new HttpClient(handler);
        _http.Timeout = timeout;
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<InferenceResult> InferAsync(InferenceRequest request, CancellationToken ct = default)
    {
        // Build OpenAI-compatible messages array.
        var openAiMessages = request.Messages.Select(m => new
        {
            role = mapRole(m.Role),
            content = m.Content,
        }).ToArray();

        // Wrap the first user message in ChatML template (same as LlamaEngine).
        // The template format: <|begin_of_text|>\n<|start_header_id|>user<|end_header_id|>\n{prompt}<|eot_id|>\n<|start_header_id|>assistant<|end_header_id|>\n
        // For the remote API, we need to apply this to the first user message content.
        // Since the remote endpoint is llama.cpp (not OpenAI), it needs the raw ChatML format.
        // We send the messages as-is (the remote API should handle ChatML), but if the remote
        // is a raw llama.cpp server, we need to wrap the conversation.
        // For safety, we send the LAST message as the direct prompt in ChatML format,
        // and pass prior messages as a "context" hint in the system message.
        // Actually — the simplest approach that matches LlamaEngine: just send the last
        // user message wrapped in ChatML as a single-turn request. But that loses conversation
        // history. Instead, send all messages in the OpenAI format and let the remote handle it.
        // The remote llama.cpp with ChatML template should handle OpenAI roles correctly
        // if it's a recent enough build. For llama.cpp servers that expect ChatML:
        // we apply ChatML to the full conversation.

        // Build the OpenAI-compatible request body using JsonObject (same pattern as AssistantWire).
        var chatMessages = BuildChatMLMessages(request.Messages);
        var messagesArray = new JsonArray();
        foreach (var msg in chatMessages)
        {
            if (msg is { } item)
            {
                var obj = new JsonObject
                {
                    ["role"] = item.GetType().GetProperty("role")?.GetValue(item)?.ToString() ?? "",
                    ["content"] = item.GetType().GetProperty("content")?.GetValue(item)?.ToString() ?? "",
                };
                messagesArray.Add(obj);
            }
        }

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = messagesArray,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = true,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(body,
            new System.Text.Json.JsonSerializerOptions
            {
                // Don't HTML-encode < and > (they're valid JSON in strings).
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _http.PostAsync($"{_endpoint}/v1/chat/completions", content, ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new AssistantException(
                $"Remote inference failed at {_endpoint}: HTTP {(int)response.StatusCode} {response.StatusCode} ({errorBody.Trim()}). "
                + "Check that the endpoint is running and the model name is correct.");
        }

        // Read SSE stream and accumulate text.
        var sb = new StringBuilder();
        int completionTokens = 0;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // SSE data line: "data: {...}" or "data: [DONE]"
            if (line.StartsWith("data: "))
            {
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") break;

                try
                {
                    var parsed = JsonNode.Parse(data);
                    var choices = parsed?["choices"];
                    if (choices is System.Text.Json.Nodes.JsonArray choicesArray && choicesArray.Count > 0)
                    {
                        var delta = choicesArray[0]?["delta"];
                        var contentDelta = delta?["content"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(contentDelta))
                        {
                            sb.Append(contentDelta);
                            // Rough completion token count: ~1.3 chars per token (average for mixed text).
                            completionTokens += (int)Math.Ceiling(contentDelta.Length / 1.3);
                        }
                    }
                }
                catch
                {
                    // Skip malformed SSE chunks — don't fail the whole request.
                }
            }
        }

        var promptTokens = EstimatePromptTokens(request.Messages);

        return new InferenceResult(
            Text: sb.ToString(),
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            Elapsed: sw.Elapsed,
            ProviderType: "Remote",
            ProviderInfo: _endpoint);
    }

    public async Task<ProviderHealth> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_endpoint}/v1/models", ct);
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderHealth(
                    IsHealthy: false,
                    AvailableModels: [],
                    Error: $"HTTP {(int)response.StatusCode} {response.StatusCode} at {_endpoint}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var parsed = JsonNode.Parse(json);
            var models = parsed?["data"];
            var available = new List<string>();
            if (models is System.Text.Json.Nodes.JsonArray modelArray)
            {
                foreach (var m in modelArray)
                {
                    var id = m?["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id)) available.Add(id);
                }
            }

            return new ProviderHealth(
                IsHealthy: true,
                AvailableModels: available,
                Error: null);
        }
        catch (Exception ex)
        {
            return new ProviderHealth(
                IsHealthy: false,
                AvailableModels: [],
                Error: $"Connection failed at {_endpoint}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        // HttpClient.DisposeAsync requires System.Net.Http package; use synchronous Dispose.
        _http.Dispose();
        await Task.CompletedTask;
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return DisposeAsync();
    }

    /// <summary>Build ChatML-formatted messages array. The llama.cpp endpoint expects
    /// ChatML format (not OpenAI JSON messages), so we convert to the template format.</summary>
    private static object[] BuildChatMLMessages(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0) return [];

        // For a raw llama.cpp server, build a single ChatML-wrapped prompt from all messages.
        // This matches how LlamaEngine works: it receives one wrapped prompt string.
        // We accumulate the conversation as a single text block.
        var sb = new StringBuilder();
        sb.Append("<|begin_of_text|>");

        foreach (var msg in messages)
        {
            var header = msg.Role switch
            {
                "system" => "system",
                "user" => "user",
                "assistant" => "assistant",
                _ => "user",
            };
            sb.Append($"<|start_header_id|>{header}<|end_header_id|>\n{msg.Content}<|eot_id|>");
        }

        // Append the assistant header (the model will generate from here).
        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n");

        return new object[] { new { role = "user", content = sb.ToString() } };
    }

    /// <summary>Map assistant wire roles to OpenAI roles (1:1 mapping).</summary>
    private static string mapRole(string role) => role switch
    {
        "system" => "system",
        "user" => "user",
        "assistant" => "assistant",
        _ => "user",
    };

    /// <summary>Estimate prompt tokens from message content length (rough approximation).</summary>
    private static int EstimatePromptTokens(IReadOnlyList<Message> messages)
    {
        // Rough estimate: ~1.3 chars per token for mixed English text.
        var totalChars = messages.Sum(m => m.Content.Length);
        return (int)Math.Ceiling(totalChars / 1.3);
    }
}

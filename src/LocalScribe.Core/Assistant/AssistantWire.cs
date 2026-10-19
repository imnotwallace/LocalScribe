// LOCKED wire contract (design 2026-07-18 section 7.1) between the App and
// LocalScribe.Assistant.exe, and the typed event surface feat/matter-qa consumes.
// Request: ONE JSON line on the helper's stdin. Events: JSON lines on its stdout.
// With keepAlive:true the helper stays resident after "done" and accepts further
// {"op":"answer",...} lines reusing the loaded model + KV prefix (warm chat);
// keepAlive:false exits after done. No sockets anywhere - stdio only.
// The SAME wire drives LocalScribe.Inference.exe, the opt-in remote helper (design 2026-10-18):
// a request carrying a "remote" block is answered by an endpoint the user configured, and the
// "transcribe" op returns one transcription event per audio segment.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalScribe.Core.Assistant;

/// <summary>One helper job request. PayloadJson is the raw JSON object embedded as
/// "payload" on the wire (v1 shape: AssistantWire.PromptPayload). Backend is the
/// REQUEST ("auto"|"cuda"|"cpu"); the backend actually used comes back on AssistantDone
/// (floor-fall provenance discipline). Remote is set only for a remote-routed job (Backend is then
/// "remote"); ModelPath is ignored by the remote helper.</summary>
public sealed record AssistantRequest(string Op, string ModelPath, int CtxTokens, string Backend, bool KeepAlive,
    string PayloadJson, RemoteEndpointConfig? Remote = null);

/// <summary>The endpoint a remote-routed request goes to (design 2026-10-18). ApiKey is the
/// DECRYPTED key: it exists only in memory and on the helper's stdin pipe, and ToString redacts it
/// so a logged request can never carry it.</summary>
public sealed record RemoteEndpointConfig(string Endpoint, string Model, string? ApiKey)
{
    public override string ToString()
        => $"RemoteEndpointConfig {{ Endpoint = {Endpoint}, Model = {Model}, ApiKey = {(ApiKey is null ? "none" : "<redacted>")} }}";
}

/// <summary>Typed stdout event stream. LOCKED contract - feat/matter-qa consumes these.</summary>
public abstract record AssistantEvent;
public sealed record AssistantChunk(string Text) : AssistantEvent;
public sealed record AssistantProgress(string Phase, int Current, int Total) : AssistantEvent;
/// <summary>Model: the model the remote endpoint reported actually serving the job (design
/// 2026-10-18) - null for local jobs, whose provenance is the manifest file + sha256.</summary>
public sealed record AssistantDone(string Backend, int PromptTokens, int OutputTokens, string? Model = null) : AssistantEvent;
public sealed record AssistantError(string Message) : AssistantEvent;
/// <summary>Embed-op result (semantic search, design 2026-07-25): one unit-normalized vector per
/// input text, plus the method tag (EmbeddingMethod.For) that gates comparability.</summary>
public sealed record AssistantEmbedResult(IReadOnlyList<float[]> Embeddings, string Method) : AssistantEvent;
/// <summary>"transcribe"-op result (remote re-transcription, design 2026-10-18): one audio
/// segment's text, the language the server detected (ISO 639-1, null when it did not say), and the
/// MINIMUM no-speech probability over the server's sub-segments (the WhisperNetEngine rule).</summary>
public sealed record AssistantTranscription(string Text, string? Language, double? NoSpeechProb) : AssistantEvent;

public static class AssistantWire
{
    /// <summary>KV-cache quantization, constant on the wire (design 7.2: KV q8_0).</summary>
    public const string KvQuant = "q8_0";

    /// <summary>Progress phase emitted when an "auto" request could not run fully on the GPU
    /// and fell to CPU (design 2026-07-23 section 5) - the recorded, never-silent fall.</summary>
    public const string CudaFellPhase = "cuda-fell-to-cpu";

    /// <summary>The Backend value of a remote-routed request and of its done event (design
    /// 2026-10-18) - stored as-is in summary and chat provenance, rendered "REMOTE".</summary>
    public const string RemoteBackend = "remote";

    /// <summary>Op name for one remote transcription of one audio segment.</summary>
    public const string TranscribeOp = "transcribe";

    /// <summary>One request, one line. JsonObject.ToJsonString() is non-indented by design -
    /// LocalScribeJson (indented, for sidecar files) must never be used on the wire.</summary>
    public static string SerializeRequest(AssistantRequest request)
    {
        var o = new JsonObject
        {
            ["op"] = request.Op,
            ["modelPath"] = request.ModelPath,
            ["ctxTokens"] = request.CtxTokens,
            ["kvQuant"] = KvQuant,
            ["backend"] = request.Backend,
            ["keepAlive"] = request.KeepAlive,
            ["payload"] = ParseOrEmpty(request.PayloadJson),
        };
        if (request.Remote is { } r)
            o["remote"] = new JsonObject { ["endpoint"] = r.Endpoint, ["model"] = r.Model, ["apiKey"] = r.ApiKey };
        return o.ToJsonString();
    }

    /// <summary>Helper-side request parse. Null on malformed/incomplete input - the helper
    /// answers with a protocol error event, it never crashes on bad stdin.</summary>
    public static AssistantRequest? ParseRequestLine(string line)
    {
        JsonObject? o = TryParseObject(line);
        if (o is null) return null;
        string? op = o["op"]?.GetValue<string>();
        string? modelPath = o["modelPath"]?.GetValue<string>();
        if (op is null || modelPath is null) return null;
        RemoteEndpointConfig? remote = o["remote"] is JsonObject r
            && r["endpoint"]?.GetValue<string>() is { } endpoint
            ? new RemoteEndpointConfig(endpoint, r["model"]?.GetValue<string>() ?? "", r["apiKey"]?.GetValue<string>())
            : null;
        return new AssistantRequest(op, modelPath,
            o["ctxTokens"]?.GetValue<int>() ?? 0,
            o["backend"]?.GetValue<string>() ?? "auto",
            o["keepAlive"]?.GetValue<bool>() ?? false,
            o["payload"]?.ToJsonString() ?? "{}",
            remote);
    }

    public static string SerializeEvent(AssistantEvent evt) => evt switch
    {
        AssistantChunk c => new JsonObject { ["type"] = "chunk", ["text"] = c.Text }.ToJsonString(),
        AssistantProgress p => new JsonObject
        { ["type"] = "progress", ["phase"] = p.Phase, ["current"] = p.Current, ["total"] = p.Total }.ToJsonString(),
        AssistantDone d => new JsonObject
        {
            ["type"] = "done",
            ["stats"] = DoneStats(d),
        }.ToJsonString(),
        AssistantError e => new JsonObject { ["type"] = "error", ["message"] = e.Message }.ToJsonString(),
        AssistantTranscription t => new JsonObject
        {
            ["type"] = "transcription", ["text"] = t.Text, ["language"] = t.Language, ["noSpeechProb"] = t.NoSpeechProb,
        }.ToJsonString(),
        AssistantEmbedResult r => new JsonObject
        {
            ["type"] = "embedResult",
            ["method"] = r.Method,
            ["embeddings"] = new JsonArray(r.Embeddings
                .Select(v => (JsonNode)new JsonArray(v.Select(f => (JsonNode)f).ToArray())).ToArray()),
        }.ToJsonString(),
        _ => throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "unknown assistant event"),
    };

    /// <summary>App-side event parse. Null on malformed/unknown lines - callers SKIP those
    /// (SherpaHelperDiariser precedent: stdout noise from native libs must never be fatal).</summary>
    public static AssistantEvent? ParseEventLine(string line)
    {
        JsonObject? o = TryParseObject(line);
        if (o is null) return null;
        return o["type"]?.GetValue<string>() switch
        {
            "chunk" => new AssistantChunk(o["text"]?.GetValue<string>() ?? ""),
            "progress" => new AssistantProgress(o["phase"]?.GetValue<string>() ?? "",
                o["current"]?.GetValue<int>() ?? 0, o["total"]?.GetValue<int>() ?? 0),
            "done" => o["stats"] is JsonObject s
                ? new AssistantDone(s["backend"]?.GetValue<string>() ?? "",
                    s["promptTokens"]?.GetValue<int>() ?? 0, s["outputTokens"]?.GetValue<int>() ?? 0,
                    s["model"]?.GetValue<string>())
                : new AssistantDone("", 0, 0),
            "error" => new AssistantError(o["message"]?.GetValue<string>() ?? ""),
            "transcription" => new AssistantTranscription(o["text"]?.GetValue<string>() ?? "",
                o["language"]?.GetValue<string>(), o["noSpeechProb"]?.GetValue<double>()),
            "embedResult" => ParseEmbedResult(o),
            _ => null,
        };
    }

    /// <summary>The v1 payload both ops use: the fully-built prompt plus an output cap.</summary>
    public static string PromptPayload(string prompt, int maxTokens)
        => new JsonObject { ["prompt"] = prompt, ["maxTokens"] = maxTokens }.ToJsonString();

    /// <summary>The transcribe-op payload: one segment as a base64 16-bit PCM WAV, the locked
    /// language (null = let the server detect) and the vocabulary prompt (null = none).</summary>
    public static string TranscribePayload(string wavBase64, string? language, string? prompt)
        => new JsonObject { ["audioWav"] = wavBase64, ["language"] = language, ["prompt"] = prompt }.ToJsonString();

    /// <summary>The embed-op payload: kind "query"|"document", the Matryoshka output dim, and the
    /// batch of texts. The HELPER applies the model's asymmetric prompt prefixes - callers never see
    /// them (design 2026-07-25).</summary>
    public static string EmbedPayload(string kind, IReadOnlyList<string> texts, int dim)
        => new JsonObject
        {
            ["kind"] = kind,
            ["dim"] = dim,
            ["texts"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()),
        }.ToJsonString();

    /// <summary>"model" is written only when present, so a local done event keeps its exact
    /// pre-remote shape on the wire.</summary>
    private static JsonObject DoneStats(AssistantDone d)
    {
        var stats = new JsonObject
        { ["backend"] = d.Backend, ["promptTokens"] = d.PromptTokens, ["outputTokens"] = d.OutputTokens };
        if (d.Model is not null) stats["model"] = d.Model;
        return stats;
    }

    private static AssistantEmbedResult? ParseEmbedResult(JsonObject o)
    {
        try
        {
            if (o["embeddings"] is not JsonArray outer)
                return null; // missing or non-array embeddings field = malformed

            var vectors = new List<float[]>();
            foreach (var row in outer)
            {
                if (row is not JsonArray inner)
                    return null; // a row that is not an array = malformed

                vectors.Add(inner.Select(n => n?.GetValue<float>() ?? 0f).ToArray());
            }
            return new AssistantEmbedResult(vectors, o["method"]?.GetValue<string>() ?? "");
        }
        catch (Exception)
        {
            return null; // GetValue throws or other exception = malformed
        }
    }

    private static JsonObject? TryParseObject(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Embeds PayloadJson as raw JSON under "payload" (not a JSON-in-JSON string).
    /// Falls back to an empty object if the caller-supplied payload string is malformed -
    /// the brief references this helper without defining it; this is the minimal
    /// implementation matching its documented behavior ("payload embedded as raw JSON").</summary>
    private static JsonNode ParseOrEmpty(string json)
    {
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}

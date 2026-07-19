// LOCKED wire contract (design 2026-07-18 section 7.1) between the App and
// LocalScribe.Assistant.exe, and the typed event surface feat/matter-qa consumes.
// Request: ONE JSON line on the helper's stdin. Events: JSON lines on its stdout.
// With keepAlive:true the helper stays resident after "done" and accepts further
// {"op":"answer",...} lines reusing the loaded model + KV prefix (warm chat);
// keepAlive:false exits after done. No sockets anywhere - stdio only.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalScribe.Core.Assistant;

/// <summary>One helper job request. PayloadJson is the raw JSON object embedded as
/// "payload" on the wire (v1 shape: AssistantWire.PromptPayload). Backend is the
/// REQUEST ("auto"|"cuda"|"cpu"); the backend actually used comes back on AssistantDone
/// (floor-fall provenance discipline).</summary>
public sealed record AssistantRequest(string Op, string ModelPath, int CtxTokens, string Backend, bool KeepAlive, string PayloadJson);

/// <summary>Typed stdout event stream. LOCKED contract - feat/matter-qa consumes these.</summary>
public abstract record AssistantEvent;
public sealed record AssistantChunk(string Text) : AssistantEvent;
public sealed record AssistantProgress(string Phase, int Current, int Total) : AssistantEvent;
public sealed record AssistantDone(string Backend, int PromptTokens, int OutputTokens) : AssistantEvent;
public sealed record AssistantError(string Message) : AssistantEvent;

public static class AssistantWire
{
    /// <summary>KV-cache quantization, constant on the wire (design 7.2: KV q8_0).</summary>
    public const string KvQuant = "q8_0";

    /// <summary>One request, one line. JsonObject.ToJsonString() is non-indented by design -
    /// LocalScribeJson (indented, for sidecar files) must never be used on the wire.</summary>
    public static string SerializeRequest(AssistantRequest request) => new JsonObject
    {
        ["op"] = request.Op,
        ["modelPath"] = request.ModelPath,
        ["ctxTokens"] = request.CtxTokens,
        ["kvQuant"] = KvQuant,
        ["backend"] = request.Backend,
        ["keepAlive"] = request.KeepAlive,
        ["payload"] = ParseOrEmpty(request.PayloadJson),
    }.ToJsonString();

    /// <summary>Helper-side request parse. Null on malformed/incomplete input - the helper
    /// answers with a protocol error event, it never crashes on bad stdin.</summary>
    public static AssistantRequest? ParseRequestLine(string line)
    {
        JsonObject? o = TryParseObject(line);
        if (o is null) return null;
        string? op = o["op"]?.GetValue<string>();
        string? modelPath = o["modelPath"]?.GetValue<string>();
        if (op is null || modelPath is null) return null;
        return new AssistantRequest(op, modelPath,
            o["ctxTokens"]?.GetValue<int>() ?? 0,
            o["backend"]?.GetValue<string>() ?? "auto",
            o["keepAlive"]?.GetValue<bool>() ?? false,
            o["payload"]?.ToJsonString() ?? "{}");
    }

    public static string SerializeEvent(AssistantEvent evt) => evt switch
    {
        AssistantChunk c => new JsonObject { ["type"] = "chunk", ["text"] = c.Text }.ToJsonString(),
        AssistantProgress p => new JsonObject
        { ["type"] = "progress", ["phase"] = p.Phase, ["current"] = p.Current, ["total"] = p.Total }.ToJsonString(),
        AssistantDone d => new JsonObject
        {
            ["type"] = "done",
            ["stats"] = new JsonObject
            { ["backend"] = d.Backend, ["promptTokens"] = d.PromptTokens, ["outputTokens"] = d.OutputTokens },
        }.ToJsonString(),
        AssistantError e => new JsonObject { ["type"] = "error", ["message"] = e.Message }.ToJsonString(),
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
                    s["promptTokens"]?.GetValue<int>() ?? 0, s["outputTokens"]?.GetValue<int>() ?? 0)
                : new AssistantDone("", 0, 0),
            "error" => new AssistantError(o["message"]?.GetValue<string>() ?? ""),
            _ => null,
        };
    }

    /// <summary>The v1 payload both ops use: the fully-built prompt plus an output cap.</summary>
    public static string PromptPayload(string prompt, int maxTokens)
        => new JsonObject { ["prompt"] = prompt, ["maxTokens"] = maxTokens }.ToJsonString();

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

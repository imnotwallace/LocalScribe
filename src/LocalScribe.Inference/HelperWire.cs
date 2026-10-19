using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalScribe.Inference;

/// <summary>The endpoint a request goes to, as the App sends it. ApiKey is the decrypted key; it
/// arrives only on stdin and ToString redacts it.</summary>
internal sealed record RemoteEndpoint(string Endpoint, string Model, string? ApiKey)
{
    /// <summary>scheme://host[:port] - what error messages name. Never the path or the key.</summary>
    public string Origin => Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri)
        ? uri.GetLeftPart(UriPartial.Authority)
        : Endpoint;

    public override string ToString()
        => $"RemoteEndpoint {{ Endpoint = {Endpoint}, Model = {Model}, ApiKey = {(ApiKey is null ? "none" : "<redacted>")} }}";
}

internal sealed record HelperRequest(string Op, bool KeepAlive, string PayloadJson, RemoteEndpoint? Remote);

/// <summary>Events the helper writes, one JSON line each.</summary>
internal abstract record HelperEvent;
internal sealed record Chunk(string Text) : HelperEvent;
internal sealed record Progress(string Phase, int Current, int Total) : HelperEvent;
internal sealed record Done(int PromptTokens, int OutputTokens, string Model) : HelperEvent;
internal sealed record Failure(string Message) : HelperEvent;
internal sealed record Transcription(string Text, string? Language, double? NoSpeechProb) : HelperEvent;

/// <summary>The subset of Core's AssistantWire contract this helper speaks, written out by hand -
/// the same trade LocalScribe.Fetch makes, for the same reason plus one: a ProjectReference to
/// Core would pull Core's whole dependency graph (ONNX Runtime, OpenXml, NAudio and the Windows
/// Desktop framework - a 134 MB single-file exe, measured) into the one process that talks to the
/// network, for the sake of a few JSON shapes. InferenceHelperTests pins this copy against Core's
/// codec in both directions, so the two cannot drift silently.</summary>
internal static class HelperWire
{
    public const string RemoteBackend = "remote";   // = AssistantWire.RemoteBackend
    public const string TranscribeOp = "transcribe"; // = AssistantWire.TranscribeOp

    /// <summary>Null on malformed input - the helper answers with a protocol error, never crashes.</summary>
    public static HelperRequest? ParseRequest(string line)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
        if (o is null || Str(o["op"]) is not { } op) return null;
        RemoteEndpoint? remote = o["remote"] is JsonObject r && Str(r["endpoint"]) is { } endpoint
            ? new RemoteEndpoint(endpoint, Str(r["model"]) ?? "", Str(r["apiKey"]))
            : null;
        bool keepAlive = o["keepAlive"] is JsonValue k && k.TryGetValue(out bool b) && b;
        return new HelperRequest(op, keepAlive, o["payload"]?.ToJsonString() ?? "{}", remote);
    }

    public static string Serialize(HelperEvent evt) => (evt switch
    {
        Chunk c => new JsonObject { ["type"] = "chunk", ["text"] = c.Text },
        Progress p => new JsonObject { ["type"] = "progress", ["phase"] = p.Phase, ["current"] = p.Current, ["total"] = p.Total },
        Done d => new JsonObject
        {
            ["type"] = "done",
            ["stats"] = new JsonObject
            {
                ["backend"] = RemoteBackend, ["promptTokens"] = d.PromptTokens,
                ["outputTokens"] = d.OutputTokens, ["model"] = d.Model,
            },
        },
        Failure f => new JsonObject { ["type"] = "error", ["message"] = f.Message },
        Transcription t => new JsonObject
        {
            ["type"] = "transcription", ["text"] = t.Text, ["language"] = t.Language, ["noSpeechProb"] = t.NoSpeechProb,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "unknown helper event"),
    }).ToJsonString();

    /// <summary>A JSON string member, or null when absent or not a string (a server's schema quirk
    /// must not crash the helper).</summary>
    public static string? Str(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static int? Int(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    public static double? Dbl(JsonNode? node)
        => node is JsonValue v && v.TryGetValue(out double d) ? d : null;
}

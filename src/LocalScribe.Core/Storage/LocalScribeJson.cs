// src/LocalScribe.Core/Storage/LocalScribeJson.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>The single JsonSerializerOptions every LocalScribe persistence path uses.</summary>
public static class LocalScribeJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        o.Converters.Add(new UtcIso8601Converter());
        o.Converters.Add(new JsonStringEnumConverter<SourceKind>());
        o.Converters.Add(new JsonStringEnumConverter<TranscriptSource>());
        o.Converters.Add(new JsonStringEnumConverter<TranscriptKind>());
        o.Converters.Add(new JsonStringEnumConverter<Medium>());
        o.Converters.Add(new JsonStringEnumConverter<RemoteMode>());
        o.Converters.Add(new JsonStringEnumConverter<MicMode>());
        o.Converters.Add(new JsonStringEnumConverter<Backend>());
        o.Converters.Add(new JsonStringEnumConverter<AppKind>());
        o.Converters.Add(new JsonStringEnumConverter<AudioFormat>());
        return o;
    }
}

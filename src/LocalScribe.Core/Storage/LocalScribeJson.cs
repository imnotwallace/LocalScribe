// src/LocalScribe.Core/Storage/LocalScribeJson.cs
using System.Text.Encodings.Web;
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
            // Relaxed: these are local truth files, never embedded in HTML/JS. The default encoder
            // escapes the chars + < > & as \uXXXX; relaxed keeps them literal so hotkeys like
            // "Ctrl+Alt+R" and free-text stay readable/faithful. Do NOT revert to Default - it
            // re-breaks SettingsTests.Roundtrips_v2_with_spec_wire_values.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
        o.Converters.Add(new JsonStringEnumConverter<ParticipantKind>());
        return o;
    }
}

// src/LocalScribe.Core/Storage/UtcIso8601Converter.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LocalScribe.Core.Storage;

/// <summary>Serializes DateTimeOffset as UTC ISO-8601 with a trailing 'Z'
/// (e.g. 2026-07-02T14:32:05Z), matching the spec timestamp shape. System.Text.Json
/// reuses this converter for DateTimeOffset? automatically.
/// Sub-second precision is INTENTIONALLY truncated on write (spec §1.2 timestamp precision):
/// milliseconds live only in durationMs/startMs/endMs, so endedAtUtc - startedAtUtc may
/// disagree with durationMs by up to 1s. Never rely on fractional seconds in *AtUtc.</summary>
public sealed class UtcIso8601Converter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ssZ";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

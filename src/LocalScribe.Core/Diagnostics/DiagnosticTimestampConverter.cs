// src/LocalScribe.Core/Diagnostics/DiagnosticTimestampConverter.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LocalScribe.Core.Diagnostics;

/// <summary>Serialises a diagnostic line's tsUtc as UTC ISO-8601 with MILLISECONDS and a trailing
/// 'Z' (e.g. 2026-08-05T09:30:00.123Z).
///
/// WHY THIS EXISTS RATHER THAN REUSING StoragePaths' UtcIso8601Converter (F19, final whole-branch
/// review; first applied 2026-08-05 as a straight reuse, REVISED 2026-08-06):
///
/// The 'Z' shape is deliberate and shared - a support engineer reads diag-yyyyMM.jsonl beside
/// session.json and meta.json, and one timestamp shape across them is worth having. What is NOT
/// shared is the truncation. UtcIso8601Converter drops sub-second precision on purpose, and its own
/// doc earns that with a companion field: "milliseconds live only in durationMs/startMs/endMs".
/// A diagnostic line has no such companion - drop them here and they are gone.
///
/// That matters for one specific, documented path. When a drain fails, DiagnosticLog.RequeueForRetry
/// puts the batch back, so a retried entry can be appended BEHIND a chronologically later one. The
/// standing ruling that this is non-corrupting reads "each line's tsUtc is still correct, so a
/// reader can re-sort". At whole-second precision every line inside the straddled second ties, and
/// a stable sort falls back to FILE order - which on exactly that path is the wrong order. The log
/// would not merely be coarser; within that second it would be confidently wrong. Pinned by
/// DiagnosticLogTests.Entries_inside_one_second_stay_separable_and_sort_back_into_order.
///
/// The nearest analogue agrees: McpAuditLog - which DiagnosticLog's own comment cites as its
/// precedent for keep-everything append-only JSONL - serialises ts_utc through no converter at all,
/// i.e. full sub-second precision. Milliseconds are the derived-log convention; whole seconds are
/// the evidentiary one.
///
/// ".fff", never ".FFF": three digits ALWAYS, including .000. Fixed width is what makes the field
/// lexicographically sortable, so a plain string sort is a chronological sort. Milliseconds, not
/// ticks, because 3 digits is enough to separate log lines and keeps the column narrow.</summary>
public sealed class DiagnosticTimestampConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>Accepts any ISO-8601 form, not just the one Write emits: files already on disk
    /// carry the two earlier shapes (System.Text.Json's "...+00:00" round-trip form, and the
    /// whole-second "Z" form written between 2026-08-05 and 2026-08-06), and this log is
    /// append-only - a month's file can hold all three.</summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture,
               DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

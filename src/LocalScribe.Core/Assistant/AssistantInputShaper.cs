using System.Text;
using System.Text.RegularExpressions;
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Assistant;

/// <summary>Summarization/Q&A input shaping (design 2026-07-18 section 7.4): leading per-line
/// timestamps stripped (line-anchored regex - a UI concern only, never content mid-line);
/// NAMED speaker labels kept with a roster preamble. LOCKED helper names - feat/matter-qa
/// consumes StripLeadingTimestamps and BuildSpeakerPreamble.</summary>
public static partial class AssistantInputShaper
{
    // Line-anchored: optional [, H:MM / HH:MM / H:MM:SS with optional .ms, optional ], then
    // trailing separators. Multiline so ^ anchors EVERY line; a timestamp mid-sentence never matches.
    [GeneratedRegex(@"^\s*\[?\d{1,2}:\d{2}(:\d{2})?([.,]\d{1,3})?\]?[\s,\-]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefix();

    public static string StripLeadingTimestamps(string text) => TimestampPrefix().Replace(text, "");

    /// <summary>Roster preamble ("Speakers in this call: A, B.") - empty roster yields "".</summary>
    public static string BuildSpeakerPreamble(IReadOnlyList<string> speakerNames)
        => speakerNames.Count == 0 ? "" : $"Speakers in this call: {string.Join(", ", speakerNames)}.";

    /// <summary>The model-facing transcript: one "Name: text" line per speaker turn, marker rows
    /// skipped (they are workflow metadata, not speech), timestamps never emitted and defensively
    /// stripped from the text itself. Verbatim otherwise - no cleanup (locked evidentiary rule).</summary>
    public static string BuildTranscriptText(IReadOnlyList<DisplayRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            if (row.IsMarker) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(row.DisplayName ?? "Unknown speaker").Append(": ")
              .Append(StripLeadingTimestamps(row.Text).Trim());
        }
        return sb.ToString();
    }
}

using System.Globalization;
using System.Linq;
using System.Text;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Projection;

/// <summary>The read view's two clipboard payloads (Tier 1 plan D, T1-9, 2026-08-05). Pure and
/// Core-side on purpose: the App test suite has no STA/dispatcher harness, so anything composed
/// inside ReadViewWindow's code-behind would be permanently untestable - the window keeps only
/// the Clipboard.SetText call.
///
/// The citation shape is:
///   "&lt;text&gt;" - &lt;speaker&gt;, &lt;HH:MM:SS&gt;, &lt;title&gt; of &lt;yyyy-MM-dd&gt; (transcript v&lt;n&gt;)
/// Every component already exists elsewhere in the product, and NONE of them is re-derived here:
/// the stamp is AssistantCitationFormat.Format (the canonical anchor - truncated, never rounded,
/// because a rounded-up anchor could point past the segment start), and the version is
/// TranscriptVersions.ShortId, the same short form MetadataFormat.VersionLine prints in exports.
/// REJECTED: TimestampFormat.Stamp - it emits mm:ss below one hour and follows the user's
/// relative/wallclock preference, so two solicitors quoting the same turn would produce two
/// different anchors. A citation must be stable.
///
/// Rows arrive pre-resolved from TranscriptProjection.Build and their Text is emitted VERBATIM -
/// never trimmed, wrapped or reflowed. Transcripts are evidence and a copy path is not allowed to
/// tidy one.</summary>
public static class TranscriptCitation
{
    /// <summary>CRLF: the clipboard's consumers here are Word, Outlook and Windows tooling - the
    /// same reasoning that put CRLF in PlainTextRenderer.Write.</summary>
    public const string Nl = "\r\n";

    /// <summary>One row as an attributable quotation. A marker row has no speaker and no
    /// evidentiary text; callers filter markers out before reaching here, and the two batch
    /// helpers below do it for them.</summary>
    public static string Format(DisplayRow row, string sessionTitle, DateTimeOffset startedAtLocal,
        string versionId)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(row.Text).Append("\" - ");
        // An unnamed turn drops the clause entirely rather than citing an empty name: an
        // unattributed line is honest, a line attributed to "" is not.
        if (!string.IsNullOrEmpty(row.DisplayName)) sb.Append(row.DisplayName).Append(", ");
        sb.Append(AssistantCitationFormat.Format(row.StartMs)).Append(", ");
        sb.Append(sessionTitle).Append(" of ");
        sb.Append(startedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Append(" (transcript ").Append(TranscriptVersions.ShortId(versionId)).Append(')');
        return sb.ToString();
    }

    /// <summary>"Copy text": the turn text alone, one row per line. No speaker and no stamp -
    /// this is literally the TEXT; attribution is what the other command is for.</summary>
    public static string PlainText(IReadOnlyList<DisplayRow> rows)
        => string.Join(Nl, rows.Where(Quotable).Select(r => r.Text));

    /// <summary>"Copy with citation": one citation per selected row, in row order, separated by a
    /// blank line so each survives being pasted into a numbered paragraph on its own.</summary>
    public static string WithCitations(IReadOnlyList<DisplayRow> rows, string sessionTitle,
        DateTimeOffset startedAtLocal, string versionId)
        => string.Join(Nl + Nl, rows.Where(Quotable)
            .Select(r => Format(r, sessionTitle, startedAtLocal, versionId)));

    /// <summary>Markers are machine bookkeeping ("Recording paused"), not evidence anyone quotes.
    /// Extended selection means one CAN be inside SelectedItems even though the row context menu
    /// is suppressed over markers, so both payloads filter.</summary>
    private static bool Quotable(DisplayRow row) => !row.IsMarker;
}

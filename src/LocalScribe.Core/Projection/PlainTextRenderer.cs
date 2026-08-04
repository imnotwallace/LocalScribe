using System.Globalization;
using System.Text;
using LocalScribe.Core.Assistant;
namespace LocalScribe.Core.Projection;

/// <summary>Renders transcript.txt - the same content as section 6 without Markdown decoration.</summary>
public static class PlainTextRenderer
{
    private const string Dot = " \u00B7 ";

    public static string Render(TranscriptHeader header, IReadOnlyList<DisplayRow> rows, string timestampsMode)
    {
        long durationMin = (long)Math.Round(header.DurationMs / 60000.0);
        var sb = new StringBuilder();
        sb.Append(header.Title).Append('\n');
        sb.Append(header.App).Append(Dot)
          .Append(header.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(Dot)
          .Append(durationMin.ToString(CultureInfo.InvariantCulture)).Append(" min").Append(Dot)
          .Append(header.Model).Append('/').Append(header.Backend).Append('\n');
        sb.Append('\n');

        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append('\n');   // blank line between sections (design 5.4 4.2)
            var row = rows[i];
            if (row.IsMarker)
                sb.Append('[').Append(row.Text).Append(']').Append('\n');
            else
                sb.Append('[').Append(TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal))
                  .Append("] ").Append(row.DisplayName).Append(": ").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>CRLF, not LF (design 2026-08-04 section 3): .txt is the format that gets pasted
    /// into Windows tooling and email. The save-time Render above keeps LF because
    /// transcript.txt's byte-identity is load-bearing.</summary>
    private const string Nl = "\r\n";

    /// <summary>Full-document EXPORT render at MarkdownRenderer.Write parity (design 2026-08-04
    /// section 3): the SAME metadata block content rules, the SAME non-optional disclaimer, the
    /// SAME cadence chunking and (cont'd) labels - undecorated, and never hard-wrapped, because a
    /// hard wrap would insert newlines into evidentiary text. Rows arrive pre-resolved from
    /// TranscriptProjection.Build and are emitted VERBATIM. The save-time Render(...) path above
    /// is a separate, untouched surface. No line numbers and no footer: .txt has no pages, so
    /// page:line citation does not exist here.</summary>
    public static string Write(TranscriptHeader header, SessionTextView meta,
        ExportProvenance provenance, ExportSummary? summary, IReadOnlyList<DisplayRow> rows,
        string timestampsMode, ExportOptions options)
    {
        var sb = new StringBuilder();
        sb.Append(meta.Title).Append(Nl).Append(Nl);
        AppendMeta(sb, "App", header.App);
        AppendMeta(sb, "Date", MetadataFormat.DateLine(meta));
        AppendMeta(sb, "Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters));
        AppendMeta(sb, "Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants));
        AppendMeta(sb, "Medium", meta.Medium);
        if (!string.IsNullOrEmpty(meta.Description)) AppendMeta(sb, "Description", meta.Description);
        AppendMeta(sb, "Transcript version", MetadataFormat.VersionLine(provenance));
        if (!string.IsNullOrEmpty(provenance.AudioFileName))
            AppendMeta(sb, "Audio", provenance.AudioFileName);
        if (!string.IsNullOrEmpty(provenance.AudioSha256))
            AppendMeta(sb, "Audio SHA-256", provenance.AudioSha256);
        string speakers = MetadataFormat.SpeakersHeard(rows);
        if (speakers.Length > 0) AppendMeta(sb, "Speakers heard", speakers);
        if (provenance.ExcerptSpan is { } excerptSpan) AppendMeta(sb, "Excerpt", excerptSpan);
        if (provenance.InProgress)
            sb.Append(Nl).Append(ExportNotices.InProgressNotice).Append(Nl);
        // Time-range excerpt (design 2026-08-04 section 8): same stacking order as the other two
        // formats - in-progress first, excerpt second.
        if (provenance.ExcerptSpan is not null)
            sb.Append(Nl).Append(ExportNotices.ExcerptNotice).Append(Nl);
        if (summary is not null)
        {
            sb.Append(Nl).Append(ExportNotices.SummaryHeading).Append(Nl);
            sb.Append(AssistantPrompts.DraftLabel).Append(Nl);
            sb.Append(summary.ProvenanceLine).Append(Nl);
            if (summary.StaleNotice is { } staleNotice) sb.Append(staleNotice).Append(Nl);
            // Independent of StaleNotice (whole-branch review fix 2): IncludeSummary and
            // ExcerptRange are orthogonal options, so a CURRENT summary in an excerpt still
            // needs this, and a stale summary in an excerpt gets both notices.
            if (provenance.ExcerptSpan is not null)
                sb.Append(ExportNotices.SummaryCoversMoreThanExcerpt).Append(Nl);
            // Normalise the stored LF to the file's CRLF - collapse any CRLF first so a
            // pre-existing "\r\n" cannot be turned into "\r\r\n" by the second Replace.
            string content = summary.ContentMarkdown.Replace("\r\n", "\n").Replace("\n", Nl).TrimEnd();
            sb.Append(Nl).Append(content).Append(Nl);
        }
        sb.Append(Nl).Append(ExportNotices.Disclaimer).Append(Nl);

        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers)
                    sb.Append(Nl).Append('[').Append(row.Text).Append(']').Append(Nl);
                continue;   // toggled-off marker: dropped entirely, no stray blank line
            }
            // Cadence chunking at MarkdownRenderer/DocxRenderer parity (design 2026-08-03
            // section 8): the three formats must not disagree about where a turn breaks, so
            // ContinuationMaxChars is shared rather than redefined here.
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0,
                DocxRenderer.ContinuationMaxChars);
            sb.Append(Nl).Append(Label(row.DisplayName, row.StartMs, options, timestampsMode,
                header.StartedAtLocal)).Append(": ").Append(chunks[0].Text).Append(Nl);
            for (int i = 1; i < chunks.Count; i++)
                sb.Append(Nl).Append(Label(row.DisplayName, chunks[i].StampMs, options,
                    timestampsMode, header.StartedAtLocal))
                  .Append(" (cont'd): ").Append(chunks[i].Text).Append(Nl);
        }
        return sb.ToString();
    }

    private static string Label(string? name, long stampMs, ExportOptions options,
        string timestampsMode, DateTimeOffset startedAtLocal)
        => options.IncludeTimestamps
            ? "[" + TimestampFormat.Stamp(stampMs, timestampsMode, startedAtLocal) + "] " + name
            : name ?? "";

    private static void AppendMeta(StringBuilder sb, string label, string value)
        => sb.Append(label).Append(": ").Append(value).Append(Nl);
}

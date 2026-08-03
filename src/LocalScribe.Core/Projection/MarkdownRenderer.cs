using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Renders transcript.md (spec section 6). Non-ASCII separators via \u escapes (ASCII source).</summary>
public static class MarkdownRenderer
{
    private const string Dot = " \u00B7 ";   // middle dot separator

    public static string Render(TranscriptHeader header, IReadOnlyList<DisplayRow> rows, string timestampsMode)
    {
        long durationMin = (long)Math.Round(header.DurationMs / 60000.0);
        var sb = new StringBuilder();
        sb.Append('#').Append(' ').Append(header.Title).Append('\n');
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
                sb.Append("_[").Append(row.Text).Append("]_").Append('\n');
            else
                sb.Append("**[").Append(TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal))
                  .Append("] ").Append(row.DisplayName).Append(":** ").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Full-document EXPORT render at DocxRenderer parity (design 2026-07-18 section 3):
    /// the SAME metadata block content rules and the SAME non-optional machine-generated
    /// disclaimer. Metadata renders as a bullet list so each line stands alone in any viewer
    /// without trailing-space hard breaks; turns and markers reuse the save-time Render dialect
    /// above, gated by the DocxOptions toggles (the options record is format-neutral and shared
    /// deliberately; TimestampIntervalMs adds stamp-only continuation paragraphs, design
    /// 2026-08-02 item 5). Rows arrive pre-resolved from TranscriptProjection.Build and are
    /// emitted VERBATIM - never filtered, cleaned, or markdown-escaped (locked evidentiary rule).
    /// The save-time Render(...) -> transcript.md path above is a separate, untouched surface.</summary>
    public static string Write(TranscriptHeader header, SessionTextView meta,
        ExportProvenance provenance, IReadOnlyList<DisplayRow> rows, string timestampsMode,
        DocxOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(meta.Title).Append('\n').Append('\n');
        AppendMeta(sb, "App", header.App);
        AppendMeta(sb, "Date",
            header.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        AppendMeta(sb, "Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters));
        AppendMeta(sb, "Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants));
        AppendMeta(sb, "Medium", meta.Medium);
        if (!string.IsNullOrEmpty(meta.Description)) AppendMeta(sb, "Description", meta.Description);
        sb.Append('\n').Append('_').Append(DocxRenderer.Disclaimer).Append('_').Append('\n');

        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers)
                    sb.Append('\n').Append("_[").Append(row.Text).Append("]_").Append('\n');
                continue;   // toggled-off marker: dropped entirely, no stray blank line
            }
            // Cadence chunking (design 2026-08-02 item 5): chunk 0 renders exactly as before;
            // later chunks are stamp-only continuation paragraphs. Interval 0 (or timestamps off)
            // yields one whole-row chunk carrying row.Text verbatim - byte-identical output.
            var chunks = TimestampCadence.Chunk(row,
                options.IncludeTimestamps ? options.TimestampIntervalMs : 0);
            string label = options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal)
                    + "] " + row.DisplayName
                : row.DisplayName ?? "";
            sb.Append('\n').Append("**").Append(label).Append(":** ").Append(chunks[0].Text).Append('\n');
            for (int i = 1; i < chunks.Count; i++)
                sb.Append('\n').Append("**[")
                  .Append(TimestampFormat.Stamp(chunks[i].StampMs, timestampsMode, header.StartedAtLocal))
                  .Append("]** ").Append(chunks[i].Text).Append('\n');
        }

        // design 2026-08-03 section 9: no footer block. The title is already the H1 above, so a
        // trailing rule + name repeated it. Markdown has no pages, so there is nothing else the
        // docx footer carried that is meaningful here.
        return sb.ToString();
    }

    private static void AppendMeta(StringBuilder sb, string label, string value)
        => sb.Append("- **").Append(label).Append(":** ").Append(value).Append('\n');
}

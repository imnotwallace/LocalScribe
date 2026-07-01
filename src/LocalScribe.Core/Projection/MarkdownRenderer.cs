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

        foreach (var row in rows)
        {
            if (row.IsMarker)
                sb.Append("_[").Append(row.Text).Append("]_").Append('\n');
            else
                sb.Append("**[").Append(TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal))
                  .Append("] ").Append(row.DisplayName).Append(":** ").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }
}

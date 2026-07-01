using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Renders transcript.txt - the same content as section 6 without Markdown decoration.</summary>
public static class PlainTextRenderer
{
    private const string Dot = " 00B7 ";

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

        foreach (var row in rows)
        {
            if (row.IsMarker)
                sb.Append('[').Append(row.Text).Append(']').Append('\n');
            else
                sb.Append('[').Append(TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal))
                  .Append("] ").Append(row.DisplayName).Append(": ").Append(row.Text).Append('\n');
        }
        return sb.ToString();
    }
}

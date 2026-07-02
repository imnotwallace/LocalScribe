using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Neutral, app-independent metadata projection - session.txt (spec section 6.2).</summary>
public sealed record SessionTextView(
    string Title,
    IReadOnlyList<string> Matters,
    IReadOnlyList<string> Participants,
    DateTimeOffset StartedAtLocal,
    DateTimeOffset? EndedAtLocal,
    long DurationMs,
    string Medium,
    string Description,
    string? Summary);

public static class SessionTextRenderer
{
    public static string Render(SessionTextView v)
    {
        long durationMin = (long)Math.Round(v.DurationMs / 60000.0);
        string dateLine = v.EndedAtLocal is { } end
            ? string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} - {end:HH:mm} ({durationMin} min)")
            : string.Create(CultureInfo.InvariantCulture,
                $"{v.StartedAtLocal:yyyy-MM-dd HH:mm} ({durationMin} min)");

        var sb = new StringBuilder();
        sb.Append(v.Title).Append('\n').Append('\n');
        sb.Append("Matter(s): ").Append(v.Matters.Count == 0 ? "(none)" : string.Join(", ", v.Matters)).Append('\n');
        sb.Append("Participants: ").Append(v.Participants.Count == 0 ? "(none)" : string.Join(", ", v.Participants)).Append('\n');
        sb.Append("Date: ").Append(dateLine).Append('\n');
        sb.Append("Medium: ").Append(v.Medium).Append('\n');
        sb.Append("Description: ").Append(string.IsNullOrEmpty(v.Description) ? "(none)" : v.Description).Append('\n');
        sb.Append("Summary: ").Append(string.IsNullOrEmpty(v.Summary) ? "(none)" : v.Summary).Append('\n');
        return sb.ToString();
    }
}

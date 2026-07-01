using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>mm:ss (or h:mm:ss >= 1h) relative to session start, or HH:mm:ss wall-clock (spec section 6).
/// Invariant culture throughout (Global Constraints): projections must render byte-identical
/// regardless of the machine's calendar or digit substitution.</summary>
public static class TimestampFormat
{
    public static string Stamp(long startMs, string mode, DateTimeOffset startedAtLocal)
    {
        if (mode == "wallclock")
            return startedAtLocal.AddMilliseconds(startMs).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var span = TimeSpan.FromMilliseconds(startMs);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes:00}:{span.Seconds:00}");
    }
}

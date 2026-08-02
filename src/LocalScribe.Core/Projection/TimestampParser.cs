using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>Inverse of <see cref="TimestampFormat.Stamp"/> for the read view's go-to box
/// (UX round 2026-08-02 item 8). Relative mode accepts m:ss / mm:ss / h:mm:ss; wallclock mode
/// accepts HH:mm:ss (a one-digit hour is tolerated) and converts via the session's local start.
/// A wallclock stamp EARLIER in the day than the session start is read as the NEXT day -
/// sessions can cross midnight, and the caller clamps to the media duration anyway.
/// Invariant culture throughout (Global Constraints); never throws - garbage returns false.</summary>
public static class TimestampParser
{
    public static bool TryParse(string? input, string mode, DateTimeOffset startedAtLocal, out long ms)
    {
        ms = 0;
        string[] parts = (input?.Trim() ?? "").Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            // NumberStyles.None: digits only - no signs, whitespace, separators; TryParse also
            // absorbs overflow-length fields as false instead of throwing.
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                return false;

        if (mode == "wallclock")
        {
            if (parts.Length != 3 || n[0] > 23 || n[1] > 59 || n[2] > 59) return false;
            var target = startedAtLocal - startedAtLocal.TimeOfDay + new TimeSpan(n[0], n[1], n[2]);
            if (target < startedAtLocal) target += TimeSpan.FromDays(1);   // crossed midnight
            ms = (long)(target - startedAtLocal).TotalMilliseconds;
            return true;
        }

        if (parts.Length == 2)                       // relative m:ss / mm:ss (minutes unbounded)
        {
            if (n[1] > 59) return false;
            ms = (n[0] * 60L + n[1]) * 1000L;
            return true;
        }
        if (n[1] > 59 || n[2] > 59) return false;    // relative h:mm:ss
        ms = ((n[0] * 60L + n[1]) * 60L + n[2]) * 1000L;
        return true;
    }
}

using System.Globalization;
using System.Text;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Deterministic session-folder id: yyyy-MM-dd_HHmm_{App}_{slug} on the LOCAL
/// wall-clock start time (spec section 9) - the caller applies the session's utcOffsetMinutes.
/// Invariant culture: folder names must be identical regardless of the machine's calendar.</summary>
public static class SessionId
{
    public static string New(DateTimeOffset startedAtLocal, AppKind app, string title)
        => $"{startedAtLocal.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)}_{app}_{Slug(title)}";

    /// <summary>Spec section 9 collision policy: same minute + app + slug gets -2, -3, ...</summary>
    public static string EnsureUnique(string candidate, Func<string, bool> exists)
    {
        if (!exists(candidate)) return candidate;
        for (int n = 2; ; n++)
        {
            string alt = $"{candidate}-{n}";
            if (!exists(alt)) return alt;
        }
    }

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        bool pendingDash = false;
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (c < 128 && char.IsLetterOrDigit(c))
            {
                if (pendingDash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                pendingDash = false;
            }
            else if (sb.Length > 0)
            {
                pendingDash = true;   // collapse runs of separators into a single dash, deferred
            }
        }
        string slug = sb.ToString();
        return slug.Length == 0 ? "session" : slug;
    }
}

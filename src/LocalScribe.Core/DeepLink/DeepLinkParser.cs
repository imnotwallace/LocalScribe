using System.Globalization;
using System.Text;

namespace LocalScribe.Core.DeepLink;

/// <summary>Typed outcome of parsing a localscribe:// URL (design 2026-07-18 section 4). A closed
/// union: the private base ctor means the only cases are the nested records below, so routing
/// switches are exhaustive by construction.</summary>
public abstract record DeepLinkResult
{
    private DeepLinkResult() { }

    /// <summary>record/start. SanitizedName is the SANITIZED name= value (Steno's contract,
    /// see DeepLinkParser), or null when absent or empty-after-sanitize.</summary>
    public sealed record StartRecording(string? SanitizedName) : DeepLinkResult;

    /// <summary>record/stop. Carries nothing - the App side must CONFIRM before stopping
    /// (a registered scheme is drive-by-invokable; stopping evidence is never silent).</summary>
    public sealed record StopRecording : DeepLinkResult;

    /// <summary>Anything off the allowlist. Reason is one of a FIXED set of constant strings -
    /// it never echoes the URL or query (query strings are never logged, design section 4).</summary>
    public sealed record Invalid(string Reason) : DeepLinkResult;
}

/// <summary>Pure parser for the localscribe:// scheme - an UNTRUSTED-INPUT boundary (any webpage
/// can invoke a registered scheme). Never throws; allowlist is exactly record/start (optional
/// name=) and record/stop; scheme/host/path/query-key are case-insensitive; one trailing '/' is
/// tolerated. Sanitization (Steno's shortcut-url contract adopted wholesale): keep Unicode
/// letters/marks/digits plus . , ( ) @ &amp; ' ! + # - ; other chars become spaces; whitespace
/// collapses; trimmed; capped at 120 chars; empty -&gt; null. '+' is a KEPT literal (percent-decoding
/// only - no form-encoding plus-to-space). Callers must never log the raw URL or query.</summary>
public static class DeepLinkParser
{
    private const string KeptPunctuation = ".,()@&'!+#-";

    public static DeepLinkResult Parse(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return new DeepLinkResult.Invalid("empty url");
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return new DeepLinkResult.Invalid("unparseable url");
            if (!string.Equals(uri.Scheme, "localscribe", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.Invalid("wrong scheme");
            if (!string.Equals(uri.Host, "record", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.Invalid("unknown host");

            string path = uri.AbsolutePath.TrimEnd('/');
            if (string.Equals(path, "/start", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.StartRecording(SanitizeName(QueryValue(uri.Query, "name")));
            if (string.Equals(path, "/stop", StringComparison.OrdinalIgnoreCase))
                return new DeepLinkResult.StopRecording();
            return new DeepLinkResult.Invalid("unknown action");
        }
        catch
        {
            // Never throws (untrusted boundary): any Uri/decoding surprise is a typed reject.
            return new DeepLinkResult.Invalid("parser fault");
        }
    }

    /// <summary>First value for <paramref name="key"/> (case-insensitive) in a raw ?a=b&amp;c=d
    /// query, percent-decoded via Uri.UnescapeDataString ('+' stays literal - it is on the keep
    /// list). Null when absent. Unknown params are ignored. Nothing here is ever logged.</summary>
    private static string? QueryValue(string query, string key)
    {
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) continue;
            return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string? SanitizeName(string? raw)
    {
        if (raw is null) return null;
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            bool keep = char.IsLetterOrDigit(c)
                || cat is UnicodeCategory.NonSpacingMark
                       or UnicodeCategory.SpacingCombiningMark
                       or UnicodeCategory.EnclosingMark
                || KeptPunctuation.Contains(c);
            sb.Append(keep ? c : ' ');
        }
        // Collapse ALL whitespace runs (incl. the spaces we just substituted) + trim in one pass.
        string joined = string.Join(' ',
            sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (joined.Length > 120) joined = joined[..120].TrimEnd();
        return joined.Length == 0 ? null : joined;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
namespace LocalScribe.Core.Assistant;

/// <summary>One parsed [HH:MM:SS] citation stamp: the bare token (no brackets) and its
/// milliseconds-from-session-start value (design 2026-07-18 section 7.5).</summary>
public sealed record CitationStamp(string Token, long Ms);

/// <summary>One line of an assistant answer, split for citation validation: the raw line, the
/// claim text with valid stamps and list markers stripped, the stamps found, and whether the
/// line is a factual CLAIM (headers, blank lines, section lead-ins and stamp-only lines are
/// not claims and are never flagged).</summary>
public sealed record AnswerLineParts(string RawLine, string ClaimText,
    IReadOnlyList<CitationStamp> Stamps, bool IsClaim);

/// <summary>The single source of truth for the citation stamp shape (design 2026-07-18 section
/// 7.5): context builders inject Format(row.StartMs) anchors per transcript line, the answer
/// prompt requires one per claim, and the validator parses the same family back. Pure.</summary>
public static class AssistantCitationFormat
{
    // [HH:MM:SS] / [H:MM:SS] / [MM:SS] / [M:SS]; range checks live in TryParseMs (the regex
    // over-matches e.g. [12:99] on purpose so an out-of-range token is REJECTED as a stamp and
    // left in the claim text rather than half-parsed).
    private static readonly Regex StampToken =
        new(@"\[(\d{1,3}(?::\d{1,2}){1,2})\]", RegexOptions.Compiled);
    private static readonly Regex ListMarker =
        new(@"^\s*(?:[-*+]|\d{1,3}[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Canonical anchor: zero-padded HH:MM:SS, invariant, truncated to whole seconds
    /// (never rounded - a rounded-up anchor could point past the segment start).</summary>
    public static string Format(long startMs)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
    }

    /// <summary>Parses a bare stamp token. Accepts HH:MM:SS / H:MM:SS / MM:SS / M:SS with
    /// hours 0-99, minutes 0-59, seconds 0-59. Never throws.</summary>
    public static bool TryParseMs(string token, out long ms)
    {
        ms = 0;
        string[] parts = token.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 2
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return false;
        }
        int hours = parts.Length == 3 ? nums[0] : 0;
        int minutes = parts.Length == 3 ? nums[1] : nums[0];
        int seconds = parts.Length == 3 ? nums[2] : nums[1];
        if (hours > 99 || minutes > 59 || seconds > 59) return false;
        ms = ((hours * 60L + minutes) * 60L + seconds) * 1000L;
        return true;
    }

    /// <summary>Every VALID bracketed stamp in the text, in order. Used by the matter-scope
    /// validator to scan included summaries for a cited time.</summary>
    public static IReadOnlyList<CitationStamp> StampsIn(string text)
    {
        var found = new List<CitationStamp>();
        foreach (Match m in StampToken.Matches(text))
            if (TryParseMs(m.Groups[1].Value, out long ms))
                found.Add(new CitationStamp(m.Groups[1].Value, ms));
        return found;
    }

    /// <summary>Line-oriented claim extraction over the answer markdown. Non-claim lines
    /// (headers, blanks, trailing-colon lead-ins, stamp-only lines) pass through unflagged;
    /// claim lines carry their stamps and the stamp/marker-stripped text the fuzzy match runs
    /// on. Invalid stamp shapes stay in the text (they are not citations).</summary>
    public static IReadOnlyList<AnswerLineParts> SplitAnswer(string answerMarkdown)
    {
        var lines = new List<AnswerLineParts>();
        foreach (string raw in answerMarkdown.Replace("\r\n", "\n").Split('\n'))
        {
            var stamps = new List<CitationStamp>();
            string stripped = StampToken.Replace(raw, m =>
            {
                if (!TryParseMs(m.Groups[1].Value, out long ms)) return m.Value;
                stamps.Add(new CitationStamp(m.Groups[1].Value, ms));
                return " ";
            });
            string claim = MultiSpace.Replace(ListMarker.Replace(stripped, ""), " ").Trim();
            bool isClaim = claim.Length > 0
                && !raw.TrimStart().StartsWith('#')
                && !claim.EndsWith(':');
            lines.Add(new AnswerLineParts(raw, claim, stamps, isClaim));
        }
        return lines;
    }
}

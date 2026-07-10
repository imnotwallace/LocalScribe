using System.Text;
namespace LocalScribe.Core.Projection;

/// <summary>Edit-distance utilities shared by the phantom-bleed dedup and the WER calculator.</summary>
public static class TextDistance
{
    public static int Levenshtein<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T>? cmp = null)
    {
        cmp ??= EqualityComparer<T>.Default;
        var prev = new int[b.Count + 1];
        var cur = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) prev[j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Count; j++)
            {
                int sub = prev[j - 1] + (cmp.Equals(a[i - 1], b[j - 1]) ? 0 : 1);
                cur[j] = Math.Min(sub, Math.Min(prev[j] + 1, cur[j - 1] + 1));
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Count];
    }

    /// <summary>Char-level similarity in [0,1] over normalized text (lowercase, punctuation
    /// collapsed to single spaces). Two empty strings are identical (1.0).</summary>
    public static double NormalizedSimilarity(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        if (na.Length == 0 && nb.Length == 0) return 1.0;
        int max = Math.Max(na.Length, nb.Length);
        return 1.0 - Levenshtein(na.ToCharArray(), nb.ToCharArray()) / (double)max;
    }

    /// <summary>Best token-window containment similarity (design 2026-07-10 section 4): the shorter
    /// normalized text scored against every same-token-count contiguous window of the longer, max
    /// taken. Catches an echo copy that picked up extra surrounding tokens (whole-string distance
    /// punishes length asymmetry: a verbatim 29-char prefix match can score 0.50). Guard: a shorter
    /// text under 12 normalized chars or 3 tokens returns 0 - interjections ("yeah", "okay") are
    /// contained in nearly everything and must never containment-match.</summary>
    public static double ContainmentSimilarity(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        string shorter = na.Length <= nb.Length ? na : nb;
        string longer = na.Length <= nb.Length ? nb : na;
        var sTok = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lTok = longer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (shorter.Length < 12 || sTok.Length < 3) return 0.0;

        double best = 0.0;
        for (int start = 0; start + sTok.Length <= lTok.Length; start++)
        {
            string window = string.Join(' ', lTok.Skip(start).Take(sTok.Length));
            int max = Math.Max(shorter.Length, window.Length);
            double sim = 1.0 - Levenshtein(shorter.ToCharArray(), window.ToCharArray()) / (double)max;
            if (sim > best) best = sim;
        }
        return best;
    }

    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                sb.Append(c);
                pendingSpace = false;
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }
}

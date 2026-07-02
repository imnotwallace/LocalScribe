using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Pipeline;

/// <summary>Word Error Rate over normalized text (design "Quality bar": regression
/// baselines, not absolute targets - thresholds live with the golden corpus).</summary>
public static class WerCalculator
{
    public static double Wer(string reference, string hypothesis)
    {
        string[] r = Split(reference);
        string[] h = Split(hypothesis);
        if (r.Length == 0) return h.Length == 0 ? 0.0 : 1.0;
        return TextDistance.Levenshtein(r, h) / (double)r.Length;
    }

    private static string[] Split(string text)
        => TextDistance.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

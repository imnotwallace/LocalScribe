// src/LocalScribe.App/ViewModels/TimestampMask.cs
namespace LocalScribe.App.ViewModels;

/// <summary>Credit-card-expiry style auto-colon input mask for the read view's go-to box (UX
/// round 2026-08-03): the user types only digits, and a colon is inserted by itself after every
/// completed pair, left-anchored, so digits never shift position ("1415" -> "14:15" as the 4th
/// digit lands, not after a trailing colon is typed). Strips every non-digit first, so pasting
/// an already-colonised stamp ("14:15", "1:02:03") or stray letters re-masks cleanly instead of
/// producing nonsense, then caps at 6 digits - hh:mm:ss (TimestampParser's longest accepted
/// shape) - so extra digits are dropped rather than accepted into a longer, meaningless run.
/// Pure/static and dependency-free: no WPF, no VM state, unit-testable directly.</summary>
public static class TimestampMask
{
    private const int MaxDigits = 6;

    public static string Format(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        Span<char> digits = stackalloc char[MaxDigits];
        int count = 0;
        foreach (char c in raw)
        {
            if (count == MaxDigits) break;
            if (c is >= '0' and <= '9') digits[count++] = c;
        }
        if (count == 0) return "";

        var result = new System.Text.StringBuilder(count + count / 2);
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && i % 2 == 0) result.Append(':');
            result.Append(digits[i]);
        }
        return result.ToString();
    }
}

// src/LocalScribe.App/ViewModels/TimestampMask.cs
namespace LocalScribe.App.ViewModels;

/// <summary>Credit-card-expiry style auto-colon input mask for the read view's go-to box (UX
/// round 2026-08-03): the user types only digits, and a colon is inserted by itself after every
/// completed pair, left-anchored, so digits never shift position ("1415" -> "14:15" as the 4th
/// digit lands, not after a trailing colon is typed). Two entry points, deliberately not shared
/// logic beyond both funnelling through the same pairing rule:
/// <list type="bullet">
/// <item><see cref="Format"/> is the TYPING path (every keystroke, via the VM's
/// OnGoToTextChanged): strips every non-digit, caps at 6 digits, re-pairs from the left. It does
/// NOT understand hours/minutes/seconds - it just flattens and repairs digits - so it must never
/// zero-pad a multi-field value: backspacing "14:15" down to "14:1" must stay "14:1", not spring
/// forward to "14:01" (a padding Format would make deleting a digit look like editing the
/// value).</item>
/// <item><see cref="Normalize"/> is the PASTE path only (wired via
/// DataObject.AddPastingHandler in the code-behind, review fix 2026-08-03): a genuine pasted
/// timestamp is a single distinct event, not a sequence of keystrokes, so it CAN safely be
/// zero-padded per field. This matters because TimestampFormat.Stamp renders relative hours
/// UNPADDED ("1:02:03" for 1h02m03s) - exactly what a user would copy out of the transcript -
/// and feeding that through Format alone silently re-pairs it into a DIFFERENT time ("10:20:3",
/// i.e. 10h20m3s) that TimestampParser accepts without error. Normalize zero-pads each field
/// first so the pasted value keeps meaning what it said.</item>
/// </list>
/// A fully zero-padded stamp is a FIXED POINT of Format's left-anchored pairing (e.g.
/// Format("01:02:03") == "01:02:03"), which is why a Normalize result is never re-mangled by the
/// VM's subsequent Format pass on the resulting Text. Pure/static and dependency-free: no WPF,
/// no VM state, unit-testable directly.</summary>
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

    /// <summary>Paste-only normalisation (see class remarks for why this must not run on every
    /// keystroke). Splits the pasted text on runs of non-digits; if that yields exactly 2 or 3
    /// all-digit fields where every field AFTER the first is 1-2 digits long (i.e. it already
    /// looks like m:ss / mm:ss / h:mm:ss / hh:mm:ss, just possibly under-padded), each field is
    /// zero-padded to at least 2 digits and rejoined with ":" - an already >= 2-digit field
    /// (including an over-length first/hours field, e.g. a 100+ hour session) is left alone,
    /// since PadLeft to a shorter width is a no-op. Anything else (wrong field count, a field
    /// with 3+ digits after the first, no digits at all) falls back to <see cref="Format"/> on
    /// the raw pasted text, same as if it had been typed.</summary>
    public static string Normalize(string? pasted)
    {
        if (string.IsNullOrEmpty(pasted)) return "";

        var fields = new System.Collections.Generic.List<string>();
        int i = 0;
        while (i < pasted.Length)
        {
            if (pasted[i] is >= '0' and <= '9')
            {
                int start = i;
                while (i < pasted.Length && pasted[i] is >= '0' and <= '9') i++;
                fields.Add(pasted[start..i]);
            }
            else i++;
        }

        bool tailFieldsAreShort = true;
        for (int f = 1; f < fields.Count; f++)
            if (fields[f].Length is not (1 or 2)) { tailFieldsAreShort = false; break; }

        if (fields.Count is not (2 or 3) || !tailFieldsAreShort) return Format(pasted);

        var result = new System.Text.StringBuilder();
        for (int f = 0; f < fields.Count; f++)
        {
            if (f > 0) result.Append(':');
            result.Append(fields[f].PadLeft(2, '0'));
        }
        return result.ToString();
    }
}

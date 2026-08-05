using System.Text;

namespace LocalScribe.Core.Diagnostics;

/// <summary>Privileged-content markers for the diagnostic log (Tier 1 plan A, 2026-08-05).
/// Settings.Logging.IncludeTranscriptText promises the user that the log does not carry transcript
/// text; that promise can only be MECHANICAL if the potentially-privileged part of a line is
/// delimited, so every call site wraps such a value in Mark(...) and Apply() is the only code that
/// ever unwraps it. REJECTED: dropping the whole Detail field when the switch is off - stack traces
/// live in Detail, and a diagnostic log with no stack traces at its DEFAULT setting is the log we
/// already had (i.e. none). REJECTED: pattern-sniffing for "natural language" - unimplementable,
/// and a guess that silently fails is worse than no guard at all.</summary>
public static class DiagnosticRedaction
{
    public const string Open = "<<";
    public const string Close = ">>";
    public const string Placeholder = "[redacted]";

    /// <summary>Wraps a value the caller believes MAY carry privileged content, NEUTRALISING any
    /// delimiter the value already contains. That neutralisation is the whole reason this is not a
    /// one-line concatenation: REJECTED plain <c>Open + value + Close</c> - Mark("a >> b") produced
    /// "&lt;&lt;a >> b>>", Apply() matched the FIRST close at index 4, emitted [redacted] and then
    /// appended " b>>" literally, putting the privileged TAIL on disk at the default setting. Email
    /// quote levels, XML/JSON fragments and C++ template text in exception messages all carry ">>".
    /// ALSO REJECTED, and the reason this spaces every bracket rather than each PAIR:
    /// <c>.Replace(Close, "> ")</c> is non-overlapping and left-to-right, so ">>>" becomes "> >>" -
    /// which re-creates the delimiter and leaks the tail again. A third-level email quote (">>>")
    /// is exactly that input. Spacing every angle bracket individually is idempotent by
    /// construction: no ">" can be followed by another ">", so no Close can survive at any run
    /// length. The cost is one space per angle bracket when IncludeTranscriptText is ON; this log
    /// is DERIVED diagnostics, never evidence, so that trade is one-way.</summary>
    public static string Mark(string? value) => Open
        + (value ?? "").Replace(">", "> ", StringComparison.Ordinal)
                       .Replace("<", "< ", StringComparison.Ordinal)
        + Close;

    /// <summary>Strips the markers when transcript text is allowed, replaces each marked run with
    /// [redacted] when it is not. An UNTERMINATED marker redacts to the end of the string - fail
    /// CLOSED, because a truncated message is exactly when leaking matters most.</summary>
    public static string? Apply(string? text, bool includeTranscriptText)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!text.Contains(Open, StringComparison.Ordinal)) return text;

        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf(Open, i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, open - i);
            int close = text.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            int contentStart = open + Open.Length;
            int contentEnd = close < 0 ? text.Length : close;
            if (includeTranscriptText) sb.Append(text, contentStart, contentEnd - contentStart);
            else sb.Append(Placeholder);
            i = close < 0 ? text.Length : close + Close.Length;
        }
        return sb.ToString();
    }

    /// <summary>The Detail string every exception call site passes to IDiagnosticLog.Write: type
    /// names and the stack UNMARKED (they carry no content), every MESSAGE marked (a message can
    /// quote a file path, a transcript line or a user's own words). REJECTED: ex.ToString() - it
    /// embeds inner-exception messages inline with no way to mark them.</summary>
    public static string ForException(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var sb = new StringBuilder();
        Exception? e = ex;
        // Depth cap: a hand-built cyclic InnerException chain would otherwise spin forever, and a
        // logger must never be the thing that hangs the app it is diagnosing.
        for (int depth = 0; e is not null && depth < 5; e = e.InnerException, depth++)
        {
            if (depth > 0) sb.Append(" ---> ");
            sb.Append(e.GetType().FullName).Append(": ").Append(Mark(e.Message));
        }
        if (ex.StackTrace is { Length: > 0 } stack) sb.Append(Environment.NewLine).Append(stack);
        return sb.ToString();
    }
}

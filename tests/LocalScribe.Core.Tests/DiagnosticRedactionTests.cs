using LocalScribe.Core.Diagnostics;

namespace LocalScribe.Core.Tests;

/// <summary>The diagnostic log's redaction contract (Tier 1 plan A, 2026-08-05). A log under the
/// storage root that captured transcript content would be an undeclared copy of privileged
/// evidence sitting outside every retention and purge path, so Settings.Logging.
/// IncludeTranscriptText has to mean something mechanical. It does: callers DELIMIT anything that
/// could be content with Mark(), and Apply() is the only thing that ever unwraps it.</summary>
public sealed class DiagnosticRedactionTests
{
    [Fact]
    public void Unmarked_text_is_untouched_in_both_directions()
    {
        Assert.Equal("gapMs=4200 device=id-headset",
            DiagnosticRedaction.Apply("gapMs=4200 device=id-headset", includeTranscriptText: false));
        Assert.Equal("gapMs=4200 device=id-headset",
            DiagnosticRedaction.Apply("gapMs=4200 device=id-headset", includeTranscriptText: true));
    }

    [Fact]
    public void Marked_runs_are_replaced_when_the_switch_is_off_and_unwrapped_when_it_is_on()
    {
        string text = "seq=7 text=" + DiagnosticRedaction.Mark("I never signed that document");
        Assert.Equal("seq=7 text=[redacted]",
            DiagnosticRedaction.Apply(text, includeTranscriptText: false));
        Assert.Equal("seq=7 text=I never signed that document",
            DiagnosticRedaction.Apply(text, includeTranscriptText: true));
    }

    [Fact]
    public void Several_marked_runs_in_one_line_are_all_handled()
    {
        string text = DiagnosticRedaction.Mark("alpha") + " | " + DiagnosticRedaction.Mark("beta");
        Assert.Equal("[redacted] | [redacted]", DiagnosticRedaction.Apply(text, false));
        Assert.Equal("alpha | beta", DiagnosticRedaction.Apply(text, true));
    }

    [Fact]
    public void An_unterminated_marker_fails_CLOSED_and_redacts_to_the_end()
    {
        // A message that happens to contain "<<" (or a truncated one) must never leak the tail.
        // REJECTED: treating an unterminated marker as literal text - that is the exact shape a
        // truncated exception message takes, which is when leaking matters most.
        Assert.Equal("head [redacted]", DiagnosticRedaction.Apply("head <<runaway content", false));
    }

    [Fact]
    public void Marked_content_containing_the_close_delimiter_does_not_leak_its_tail()
    {
        // The one case where the marker scheme could fail OPEN. Without neutralisation Mark()
        // produced "<<a >> b>>", Apply found the FIRST ">>" at index 4, emitted [redacted] and then
        // appended " b>>" LITERALLY - privileged tail on disk at the default setting. Real inputs
        // that carry ">>": quoted email levels, XML/JSON fragments, C++ template text in an
        // exception message. Mark() neutralises BOTH delimiters before wrapping.
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >> b"), false));
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a << b"), false));
        // ODD-LENGTH RUNS - the case that broke the FIRST attempt at this fix. A pairwise
        // .Replace(">>", "> >") is non-overlapping and left-to-right, and its replacement string
        // ENDS in ">", so ">>>" became "> >" + ">" = "> >>" - the delimiter re-formed at the join
        // and the tail leaked exactly as before. A THIRD-LEVEL EMAIL QUOTE (">>>") is that input
        // verbatim. Spacing every bracket INDIVIDUALLY cannot re-form a pair at any run length,
        // which is why Mark() does that rather than looping the pairwise replace.
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >>> b"), false));
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark(">>>> quoted"), false));
        Assert.Equal("[redacted]", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a <<< b"), false));
        // The documented COST, asserted exactly so nobody "tidies" it later: with the switch ON,
        // EVERY angle bracket comes back with a trailing space, so a bracket that was already
        // followed by a space yields TWO. That is not a bug - it is the neutralisation being
        // idempotent by construction. This log is DERIVED diagnostics, never evidence, so altered
        // punctuation in a diagnostic message is acceptable where a leak is not.
        Assert.Equal("a > >  b", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >> b"), true));
        Assert.Equal("a > > >  b", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a >>> b"), true));
        Assert.Equal("a < <  b", DiagnosticRedaction.Apply(DiagnosticRedaction.Mark("a << b"), true));
    }

    [Fact]
    public void Null_and_empty_survive_unchanged()
    {
        Assert.Null(DiagnosticRedaction.Apply(null, false));
        Assert.Equal("", DiagnosticRedaction.Apply("", true));
    }

    [Fact]
    public void ForException_marks_every_message_and_leaves_the_stack_readable()
    {
        Exception caught;
        try
        {
            try { throw new InvalidOperationException("witness said I never signed that"); }
            catch (Exception inner) { throw new ApplicationException("save failed", inner); }
        }
        catch (Exception ex) { caught = ex; }

        string detail = DiagnosticRedaction.ForException(caught);
        Assert.Contains("System.ApplicationException", detail);
        Assert.Contains("System.InvalidOperationException", detail);   // inner types are kept
        Assert.Contains("ForException_marks_every_message", detail);    // the stack IS present

        string redacted = DiagnosticRedaction.Apply(detail, includeTranscriptText: false)!;
        Assert.DoesNotContain("never signed", redacted);                // BOTH messages are gone
        Assert.DoesNotContain("save failed", redacted);
        Assert.Contains("System.ApplicationException", redacted);       // ...types and stack stay
        Assert.Contains("ForException_marks_every_message", redacted);
    }

    [Fact]
    public void Levels_rank_from_error_to_debug_and_unknown_reads_as_info()
    {
        Assert.Equal(0, DiagnosticLevels.Rank(DiagnosticLevels.Error));
        Assert.Equal(1, DiagnosticLevels.Rank(DiagnosticLevels.Warn));
        Assert.Equal(2, DiagnosticLevels.Rank(DiagnosticLevels.Info));
        Assert.Equal(3, DiagnosticLevels.Rank(DiagnosticLevels.Debug));
        Assert.Equal(2, DiagnosticLevels.Rank("  INFO "));   // settings.json is hand-editable
        Assert.Equal(2, DiagnosticLevels.Rank("verbose"));   // unknown -> the documented default
        Assert.Equal(2, DiagnosticLevels.Rank(null));
    }
}

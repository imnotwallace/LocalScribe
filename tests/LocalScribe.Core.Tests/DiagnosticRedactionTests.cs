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

    // Named helper so its frame appears ONLY in the inner exception's own stack trace, never in
    // the outer wrapper's - the outer ApplicationException below is thrown from a DIFFERENT
    // statement in the test method, so its stack trace cannot accidentally contain this name. That
    // is what lets the test below actually distinguish "outer stack only" from "every level's
    // stack", which the previous version (both throws inline in the same method) could not do.
    private static void ThrowWitnessStatement() =>
        throw new InvalidOperationException("witness said I never signed that");

    [Fact]
    public void ForException_marks_every_message_and_leaves_the_stack_readable()
    {
        // Initialised (not just declared): ThrowWitnessStatement() is a method CALL, not an inline
        // throw statement, so the compiler's definite-assignment analysis cannot prove the try
        // block always throws and refuses to compile an unassigned 'caught' on the fall-through path.
        Exception caught = null!;
        try
        {
            try { ThrowWitnessStatement(); }
            catch (Exception inner) { throw new ApplicationException("save failed", inner); }
        }
        catch (Exception ex) { caught = ex; }

        string detail = DiagnosticRedaction.ForException(caught);
        Assert.Contains("System.ApplicationException", detail);
        Assert.Contains("System.InvalidOperationException", detail);   // inner types are kept
        Assert.Contains("ForException_marks_every_message", detail);    // the OUTER stack is present
        // The FAULT SITE, not just the catch site: only the inner exception's own stack trace
        // contains this frame, so this assertion is blind unless every level's stack is appended.
        Assert.Contains(nameof(ThrowWitnessStatement), detail);

        string redacted = DiagnosticRedaction.Apply(detail, includeTranscriptText: false)!;
        Assert.DoesNotContain("never signed", redacted);                // BOTH messages are gone
        Assert.DoesNotContain("save failed", redacted);
        Assert.Contains("System.ApplicationException", redacted);       // ...types and stack stay
        Assert.Contains("ForException_marks_every_message", redacted);
        Assert.Contains(nameof(ThrowWitnessStatement), redacted);        // the fault site survives too
    }

    [Fact]
    public async Task ForException_neutralises_a_doubled_bracket_stack_frame_so_it_is_not_swallowed()
    {
        // C# compiles an async lambda's state machine to a DOUBLED-angle-bracket frame name -
        // MEASURED on this build: "at ...<>c.<<MethodName>b__0_0>d.MoveNext() ... --- End of stack
        // trace from previous location --- at ...MethodName() ...". That is a literal "<<" with no
        // ">>" after it on the same frame. Built via a REAL async lambda, not a hand-built string,
        // so this pins actual compiler output rather than an assumption about it. Before this fix,
        // ForException appended the stack unneutralised, so Apply() read that "<<" as an
        // unterminated marker and failed CLOSED on it - which redacts everything after it,
        // including the frame(s) below, at the DEFAULT (includeTranscriptText: false) setting. That
        // is backwards: a stack trace carries no privileged content and must always survive.
        Func<Task> throwingLambda = async () => throw new InvalidOperationException("boom");
        Exception caught = null!;
        try { await throwingLambda(); }
        catch (Exception ex) { caught = ex; }

        // Sanity: the doubled bracket really is in the raw stack, so the rest of this test is
        // actually exercising the failure mode and not a compiler artifact that stopped occurring.
        Assert.Contains("<<", caught.StackTrace!);

        string detail = DiagnosticRedaction.ForException(caught);
        Assert.Contains("MoveNext", detail);

        string redacted = DiagnosticRedaction.Apply(detail, includeTranscriptText: false)!;
        // The frame text AFTER the doubled bracket must still be present - not swallowed into a
        // single [redacted] that ate the rest of the line. This is the assertion that fails on the
        // unfixed code (it produces "...boom[redacted]" with MoveNext gone).
        Assert.Contains("MoveNext", redacted);
        Assert.Contains(nameof(ForException_neutralises_a_doubled_bracket_stack_frame_so_it_is_not_swallowed), redacted);
        // The actual exception message is still redacted at the default setting - this test proves
        // the stack survives, not that redaction stopped working.
        Assert.DoesNotContain("boom", redacted);
        Assert.Contains(DiagnosticRedaction.Placeholder, redacted);
    }

    [Fact]
    public void Capture_fault_message_is_marked_so_a_path_bearing_exception_does_not_reach_disk()
    {
        // I-1 fix (review round 1, 2026-08-05): pins ProcessLoopbackCapture.PumpLoop's exact
        // catch-block composition - that class cannot be unit-tested directly (it activates real
        // WASAPI, see CaptureDiagnosticsTests' class doc comment), so the composed SHAPE is pinned
        // here instead. REJECTED leaving ex.Message unmarked: that was "safe" only because nothing
        // in the capture path currently throws a path-bearing exception, but the catch wraps
        // FrameAvailable?.Invoke, which runs arbitrary subscriber code, and
        // SpikeRunner/Program.cs:200 already attaches a disk-writing sink to that event - "a
        // FrameAvailable handler that does file IO and can throw an IOException naming the file"
        // is a shape this repo writes today.
        string line = "device invalidated (0x88890004): " +
            DiagnosticRedaction.Mark(
                @"IOException: could not write C:\Users\sam\LocalScribe\sessions\2026-08-05_1430_Webex_Smith-v-Jones\remote.wav") +
            " - recovering";

        string redacted = DiagnosticRedaction.Apply(line, includeTranscriptText: false)!;
        // The privileged part - the session folder name, which embeds the matter/client name
        // (SessionId.cs: yyyy-MM-dd_HHmm_{App}_{Slug(title)}) - is gone at the default setting...
        Assert.DoesNotContain("Smith-v-Jones", redacted);
        Assert.DoesNotContain(@"C:\Users", redacted);
        // ...but the diagnostic SIGNAL this task exists to capture - classification, HRESULT, and
        // recovery state - survives untouched, because only the free-text message was marked.
        Assert.Equal("device invalidated (0x88890004): [redacted] - recovering", redacted);

        // And with the switch on, the same line is fully readable - the marker is round-trippable,
        // not a permanent loss.
        string unredacted = DiagnosticRedaction.Apply(line, includeTranscriptText: true)!;
        Assert.Contains("Smith-v-Jones", unredacted);
        Assert.Contains("device invalidated (0x88890004):", unredacted);
    }

    [Fact]
    public void Capture_fault_message_containing_a_marker_delimiter_does_not_eat_the_HRESULT()
    {
        // The INVERSE risk on the same composed line (I-1): COM and native error messages quote
        // template/XML fragments, so an unmarked ex.Message containing "<<" would trip Apply()'s
        // fail-closed unterminated-marker path and redact everything after it - eating the HRESULT
        // and " - recovering" too, which is the exact failure shape this plan has already been
        // bitten by twice. Mark() neutralises the value's OWN delimiters before wrapping (see
        // Mark()'s doc comment), so a delimiter inside the exception message cannot do this.
        string line = "capture error (0x8000FFFF): " +
            DiagnosticRedaction.Mark("native error <<template mismatch>>") +
            " - recovering";

        Assert.Equal("capture error (0x8000FFFF): [redacted] - recovering",
            DiagnosticRedaction.Apply(line, includeTranscriptText: false));
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

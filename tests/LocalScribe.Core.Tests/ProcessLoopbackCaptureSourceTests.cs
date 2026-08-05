namespace LocalScribe.Core.Tests;

/// <summary>Source-text pins for ProcessLoopbackCapture's diagnostic-throttling logic (review
/// rounds 2-3, 2026-08-05, on Tier 1 plan A Task 9). ProcessLoopbackCapture activates real WASAPI
/// and cannot be driven in a unit test - PumpLoop and DrainPackets are reachable only through
/// Start(), which blocks on real hardware (see CaptureDiagnosticsTests' class doc comment for the
/// same constraint). A text assertion on the actual source is the only guard available for this
/// class's control flow, the same convention
/// AssistantPublishLayoutTests.Guard_script_lists_every_required_path_verbatim uses for
/// tools/verify-assistant-publish.ps1. If one of these fails after a refactor, re-point the pin -
/// do not delete it and do not delete the throttling it guards.</summary>
public sealed class ProcessLoopbackCaptureSourceTests
{
    private static string Source()
    {
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx"))) { repo = d.FullName; break; }
        Assert.NotNull(repo);
        return File.ReadAllText(Path.Combine(
            repo!, "src", "LocalScribe.Core", "Audio", "ProcessLoopbackCapture.cs"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    [Fact]
    public void Activation_line_only_logs_when_the_format_actually_changed()
    {
        // I-2 fix round 2 (review round 2, 2026-08-05): round 1's unconditional
        // Diag("activated: " + ActivationInfo) reintroduced the flood I-3 exists to close, through
        // a different door - ActivateAndInitialize also runs on every pump-loop RE-activation, so a
        // persistent post-activation fault re-emitted this line roughly once a second. This pins
        // the fix: log on the first activation (the cache starts null), or when ActivationInfo
        // differs from the last value actually logged - and stays silent otherwise. Untouched by
        // round 3 (that round only touched the two count-based throttles below).
        string src = Source();
        Assert.Contains("private string? _lastLoggedActivationInfo;", src);
        Assert.Contains("if (_lastLoggedActivationInfo != ActivationInfo)", src);
        Assert.Contains("_lastLoggedActivationInfo = ActivationInfo;", src);

        // The guard must actually wrap the Diag call (not just exist somewhere else in the file) -
        // and the cache-update must happen INSIDE the guard, or every activation would still log.
        int guardIndex = src.IndexOf("if (_lastLoggedActivationInfo != ActivationInfo)", StringComparison.Ordinal);
        int diagIndex = src.IndexOf("Diag(\"activated: \" + ActivationInfo);", StringComparison.Ordinal);
        int cacheIndex = src.IndexOf("_lastLoggedActivationInfo = ActivationInfo;", StringComparison.Ordinal);
        Assert.True(guardIndex >= 0 && diagIndex > guardIndex && diagIndex - guardIndex < 300,
            "the activated: Diag call must be inside the format-changed guard");
        Assert.True(cacheIndex > diagIndex && cacheIndex - diagIndex < 100,
            "the cache must be updated right after logging, inside the same guard");
    }

    [Fact]
    public void Discontinuity_throttle_is_wall_clock_not_a_packet_count()
    {
        // I-3 fix round 3 (review round 3, 2026-08-05): a COUNTER cannot express "one line per
        // episode" no matter which reset rule is chosen - round 1's lifetime count (never reset)
        // could swallow a genuinely new, isolated discontinuity FOREVER if it landed off a multiple
        // of the threshold; round 2's fix (reset to 0 on any clean packet) then let an alternating
        // dirty/clean/dirty/clean pattern - a real device/driver hiccup shape - fire the "first
        // occurrence" branch on EVERY event, up to ~100/second, defeating the throttle entirely.
        // Only wall-clock time bounds the rate under every packet pattern. This pins the fix AND
        // the absence of both rejected forms, so a revert to either one fails this test.
        string src = Source();

        Assert.Contains("private long? _lastDiscontinuityLogTicks;", src);
        Assert.Contains("private const long DiagnosticThrottleIntervalMs = 30_000;", src);
        Assert.Contains(
            "now - _lastDiscontinuityLogTicks.Value >= DiagnosticThrottleIntervalMs", src);

        // Round 1's gate condition and round 2's modulo must both be GONE, not merely
        // supplemented - the bug in each was the count-based CONDITION itself, not a missing
        // extra check alongside it.
        Assert.DoesNotContain("_discontinuityCount == 0 ||", src);
        Assert.DoesNotContain("% 6000", src);

        // Exactly one increment (unconditional per discontinuity-flagged packet) and exactly one
        // reset (inside the time-gate, after logging) - round 2's per-CLEAN-packet reset and
        // DropClient's belt-and-braces reset would each add a SECOND "= 0;" site; catching the
        // count, not just presence, is what actually distinguishes this shape from round 2's.
        Assert.Equal(1, Occurrences(src, "_discontinuityCount++;"));
        Assert.Equal(1, Occurrences(src, "_discontinuityCount = 0;"));

        // The informational count is included in the line actually written to disk.
        Assert.Contains("since last report", src);
    }

    [Fact]
    public void Fault_line_throttle_is_wall_clock_not_the_reset_prone_errors_counter()
    {
        // I-3 fix round 3 (review round 3, 2026-08-05): TRACED per the coordinator's request -
        // `errors = 0` (PumpLoop) runs after ANY successful try-block pass, so a flaky endpoint
        // that alternates "reactivates fine" / "fails again immediately" resets `errors` to 0
        // before every single failure. Round 1's `errors == 0 || errors % 60 == 0` gate then fired
        // on EVERY fault under that pattern - the exact same packet-parity flaw the discontinuity
        // counter had, just from the success side. Fixed the same way: a wall-clock gate sharing
        // DiagnosticThrottleIntervalMs with the discontinuity site.
        string src = Source();

        Assert.Contains("long? lastFaultLogTicks = null;", src);
        Assert.Contains(
            "if (lastFaultLogTicks is null || now - lastFaultLogTicks.Value >= DiagnosticThrottleIntervalMs)",
            src);

        // Round 1's counter-based gate must be GONE from the executable code (it may still be
        // named in an explanatory comment, which never has "if (" immediately before it).
        Assert.DoesNotContain("if (errors == 0 || errors % 60 == 0)", src);

        // `errors` must still drive the backoff sleep - round 3 changed ONLY the logging gate, not
        // the backoff that keeps a persistent fault from hot-looping.
        Assert.Contains(
            "if (++errors > 1) Thread.Sleep(Math.Min(1000, 150 * (errors - 1)));", src);
    }
}

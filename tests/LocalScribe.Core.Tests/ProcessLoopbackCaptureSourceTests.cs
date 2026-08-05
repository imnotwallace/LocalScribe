using System.Reflection;
using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Tests;

/// <summary>Source-text pins for ProcessLoopbackCapture's diagnostic-throttling logic (review
/// rounds 2-3, 2026-08-05, on Tier 1 plan A Task 9). ProcessLoopbackCapture activates real WASAPI
/// and cannot be driven in a unit test - PumpLoop and DrainPackets are reachable only through
/// Start(), which blocks on real hardware (see CaptureDiagnosticsTests' class doc comment for the
/// same constraint). A text assertion on the actual source is the only guard available for this
/// class's control flow, the same convention
/// AssistantPublishLayoutTests.Guard_script_lists_every_required_path_verbatim uses for
/// tools/verify-assistant-publish.ps1. If one of these fails after a refactor, re-point the pin -
/// do not delete it and do not delete the throttling it guards.
///
/// ONE fact here is genuinely behavioural rather than textual (F10, final whole-branch review,
/// 2026-08-05): the ctor touches no hardware, so an inert instance can be constructed and its
/// private Diag() driven by reflection. Everything reachable only through Start() still cannot be.</summary>
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

        // Exactly one increment (unconditional per discontinuity-flagged packet) and - since F16
        // below - NO zeroing site at all. Catching the COUNT, not just presence, is what
        // distinguishes this shape from round 2's per-clean-packet reset.
        Assert.Equal(1, Occurrences(src, "_discontinuityCount++;"));
        Assert.Equal(0, Occurrences(src, "_discontinuityCount = 0;"));

        // The informational count is included in the line actually written to disk.
        Assert.Contains("_discontinuityCount +", src);
    }

    [Fact]
    public void The_discontinuity_count_is_cumulative_so_no_episode_tail_is_dropped()
    {
        // F16 (final whole-branch review, 2026-08-05). The count used to be "events since the last
        // line actually logged" and was zeroed inside the 30 s time gate right after logging, so
        // the tail of every episode vanished: three discontinuities inside two seconds during a
        // 90-minute deposition emitted exactly ONE line claiming a count of 1, and the two further
        // silence insertions into an evidentiary recording were recorded NOWHERE. A support
        // engineer reads that and concludes a single blip. A running total cannot lose a tail.
        //
        // The THROTTLE itself is unchanged - the three facts above still pin the wall-clock gate
        // that took three fix rounds to get right (sustained, alternating dirty/clean, and
        // isolated-blip patterns all stay bounded); only the number printed inside the line moved.
        string src = Source();

        Assert.Contains("\" total)\"", src);
        Assert.DoesNotContain("since last report", src);
        // No zeroing site anywhere - not in the gate (round 3's shape), not at Start(), not at the
        // DropClient/stream boundary (whose own comment argues correctly against boundary resets).
        Assert.DoesNotContain("_discontinuityCount = 0", src);
        // The wall-clock gate must still be what decides IF a line is emitted; F16 changed only
        // WHAT the emitted line says.
        Assert.Contains(
            "now - _lastDiscontinuityLogTicks.Value >= DiagnosticThrottleIntervalMs", src);
        Assert.Contains("_lastDiscontinuityLogTicks = now;", src);
    }

    [Fact]
    public void Diagnostic_message_prefixes_are_the_severity_vocabulary_the_app_sink_maps()
    {
        // F3 (final whole-branch review, 2026-08-05). This class emits three genuinely different
        // severities through ONE Action<string> event and encodes the severity in the message
        // TEXT. CompositionRoot.CaptureDiagnosticLevel (App) branches on these exact prefixes:
        // "capture error"/"device invalidated" -> error (so a capture fault can latch
        // DiagnosticLog.LastError and reach Settings' "Copy last error"), "data discontinuity" ->
        // warn (evidentiary, survives a warn-level filter, never clobbers a real error),
        // everything else -> info. A silent rename here would downgrade a capture FAULT to info,
        // where LastError can never latch it - which is the exact defect F3 fixed. This is the
        // Core half of the two-sided pin; CompositionRootTests holds the App half.
        string src = Source();

        Assert.Contains("\"device invalidated\" : \"capture error\"", src);
        Assert.Contains("Diag(\"data discontinuity at devicePos \"", src);
        Assert.Contains("Diag(\"activated: \" + ActivationInfo);", src);

        // Both fault sites sit behind the SAME 30 s wall-clock throttle, which is what makes
        // error-level safe here: it can neither flood a never-pruned file nor thrash LastError.
        int fault = src.IndexOf("if (lastFaultLogTicks is null ||", StringComparison.Ordinal);
        int diag = src.IndexOf("Diag((IsInvalidation(ex)", StringComparison.Ordinal);
        Assert.True(fault >= 0 && diag > fault && diag - fault < 400,
            "the error-level capture fault line must stay inside the wall-clock throttle gate");
    }

    [Fact]
    public void A_throwing_diagnostic_subscriber_can_never_reach_the_pump_loop()
    {
        // F10 (final whole-branch review, 2026-08-05). Diag() is called from INSIDE PumpLoop's
        // catch block, whose own comment states the invariant: "Recovery must NEVER throw out of
        // the loop - that would kill the pump thread and with it WavSink.Dispose, corrupting both
        // recordings." Diagnostic is a PUBLIC event invoking arbitrary subscriber code, so an
        // unguarded invoke lets a subscriber fault escape that catch, terminate the pump thread
        // and - an unhandled exception on a background thread - take the process down
        // mid-recording. Driven by reflection because Diag is private and the only public routes
        // to it (PumpLoop/DrainPackets) are reachable only through Start(), which activates real
        // WASAPI; the ctor itself touches no hardware, so the instance below is inert.
        using var capture = new ProcessLoopbackCapture(1234, new StopwatchClock());
        capture.Diagnostic += _ => throw new InvalidOperationException("subscriber blew up");

        var diag = typeof(ProcessLoopbackCapture).GetMethod(
            "Diag", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(diag);

        // No throw, no TargetInvocationException: the guard must swallow it entirely.
        diag!.Invoke(capture, new object?[] { "capture error (0x88890004): boom - recovering" });

        // And the source-text half, so a refactor that drops the try/catch fails even if the
        // reflection route above is ever changed.
        string src = Source();
        Assert.Contains("try { Diagnostic?.Invoke(message); }", src);
        Assert.DoesNotContain("private void Diag(string message) => Diagnostic?.Invoke(message);", src);
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

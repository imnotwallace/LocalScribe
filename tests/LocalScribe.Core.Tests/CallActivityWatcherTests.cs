using LocalScribe.Core.Live;

public class CallActivityWatcherTests
{
    // Design 2026-07-18 section 5.1: poll-and-diff over ACTIVE capture-endpoint sessions, the
    // Windows analog of Steno's mic-monitor. The WASAPI walk hides behind IAudioSessionScanner,
    // so every diff branch is deterministic here; the real DataFlow.Capture walk is smoke-only
    // (Humble Object, like WasapiSessionScanner's render path).

    private sealed class ScriptedScanner : IAudioSessionScanner
    {
        public List<AudioSessionInfo> Active { get; } = new();
        public bool Throw;
        public IReadOnlyList<AudioSessionInfo> Scan()
            => Throw ? throw new InvalidOperationException("COM enumeration failed") : Active.ToList();
    }

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);

    private static (CallActivityWatcher Watcher, ScriptedScanner Scanner, ManualUtcTimeProvider Time,
        List<CallAppActivity> Events) Make()
    {
        var scanner = new ScriptedScanner();
        var time = new ManualUtcTimeProvider(T0);
        var watcher = new CallActivityWatcher(scanner, time);
        var events = new List<CallAppActivity>();
        watcher.Activity += events.Add;
        return (watcher, scanner, time, events);
    }

    [Fact]
    public void First_poll_reports_each_active_session_as_started_with_the_tick_timestamp()
    {
        var (watcher, scanner, _, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        scanner.Active.Add(new AudioSessionInfo(202, "chrome"));
        watcher.Poll();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e is { Exe: "CiscoCollabHost", Pid: 101, Kind: CallAppActivityKind.Started });
        Assert.Contains(events, e => e is { Exe: "chrome", Pid: 202, Kind: CallAppActivityKind.Started });
        Assert.All(events, e => Assert.Equal(T0, e.Timestamp));
    }

    [Fact]
    public void Unchanged_set_raises_nothing()
    {
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        time.Set(T0 + TimeSpan.FromSeconds(1.5));
        watcher.Poll();
        Assert.Empty(events);
    }

    [Fact]
    public void A_session_leaving_reports_stopped_with_the_observing_ticks_timestamp()
    {
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        scanner.Active.Clear();
        time.Set(T0 + TimeSpan.FromSeconds(3));
        watcher.Poll();
        var e = Assert.Single(events);
        Assert.Equal(("CiscoCollabHost", 101, CallAppActivityKind.Stopped, T0 + TimeSpan.FromSeconds(3)),
            (e.Exe, e.Pid, e.Kind, e.Timestamp));
    }

    [Fact]
    public void Scanner_error_skips_the_tick_and_never_fabricates_stopped_events()
    {
        // Fail-open (locked rule): a COM hiccup must not look like every call ending at once - a
        // fabricated Stopped would arm the 3 s call-end debounce off a transient error. The next
        // successful poll diffs against the PRE-error baseline, so a session that genuinely
        // survived the hiccup raises nothing.
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        scanner.Throw = true;
        watcher.Poll();
        Assert.Empty(events);                                     // error tick: no events at all
        scanner.Throw = false;
        time.Set(T0 + TimeSpan.FromSeconds(3));
        watcher.Poll();
        Assert.Empty(events);                                     // survived the hiccup: still no diff
        Assert.Contains("CiscoCollabHost", watcher.ActiveExes);
    }

    [Fact]
    public void Diff_is_per_pid_so_a_second_process_of_the_same_exe_still_reports()
    {
        var (watcher, scanner, _, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "ms-teams"));
        watcher.Poll();
        events.Clear();
        scanner.Active.Add(new AudioSessionInfo(102, "ms-teams"));
        watcher.Poll();
        var e = Assert.Single(events);
        Assert.Equal((102, CallAppActivityKind.Started), (e.Pid, e.Kind));
    }

    [Fact]
    public void Reset_clears_the_baseline_so_the_next_poll_rereports_started()
    {
        // Master toggle off -> Reset + no polling; toggling back on re-reports the then-active
        // sessions as fresh Starts (policy cooldown dedups offers) instead of diffing stale state.
        var (watcher, scanner, _, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "Zoom"));
        watcher.Poll();
        events.Clear();
        watcher.Reset();
        Assert.Empty(watcher.ActiveExes);
        watcher.Poll();
        var e = Assert.Single(events);
        Assert.Equal(("Zoom", CallAppActivityKind.Started), (e.Exe, e.Kind));
    }

    [Fact]
    public void ActiveExes_deduplicates_images_across_pids()
    {
        var (watcher, scanner, _, _) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "ms-teams"));
        scanner.Active.Add(new AudioSessionInfo(102, "ms-teams"));
        scanner.Active.Add(new AudioSessionInfo(103, "Zoom"));
        watcher.Poll();
        Assert.Equal(2, watcher.ActiveExes.Count);
        Assert.Contains("ms-teams", watcher.ActiveExes);
        Assert.Contains("Zoom", watcher.ActiveExes);
    }

    [Fact]
    public void One_pid_leaving_and_a_different_pid_joining_in_the_same_tick_reports_both()
    {
        // The diff is a plain two-way set comparison against the PID keyspace, so a leave and a
        // join landing in the same poll are independent branches - both must fire from one Poll().
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        scanner.Active.Clear();
        scanner.Active.Add(new AudioSessionInfo(202, "chrome"));
        time.Set(T0 + TimeSpan.FromSeconds(1.5));
        watcher.Poll();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e is { Exe: "CiscoCollabHost", Pid: 101, Kind: CallAppActivityKind.Stopped });
        Assert.Contains(events, e => e is { Exe: "chrome", Pid: 202, Kind: CallAppActivityKind.Started });
        Assert.All(events, e => Assert.Equal(T0 + TimeSpan.FromSeconds(1.5), e.Timestamp));
    }

    [Fact]
    public void A_surviving_pid_that_swaps_to_a_different_exe_raises_nothing()
    {
        // Fail-safe bias, pinned intentionally: the diff keys ONLY on PID presence (see Poll()),
        // never on the (pid, exe) pair. A reused PID mapping to a different exe between ticks -
        // e.g. the OS recycling a PID right as one app exits and another launches - is therefore
        // silently ignored: neither a Stopped for the old exe nor a Started for the new one fires.
        // This can only MISS a transition, never fabricate one, matching the scanner-error
        // fail-open contract above. A real PID-collision-within-one-poll-interval is rare enough
        // that under-reporting is the safer failure mode for an advisory-only feature.
        var (watcher, scanner, time, events) = Make();
        scanner.Active.Add(new AudioSessionInfo(101, "CiscoCollabHost"));
        watcher.Poll();
        events.Clear();
        scanner.Active.Clear();
        scanner.Active.Add(new AudioSessionInfo(101, "chrome"));
        time.Set(T0 + TimeSpan.FromSeconds(1.5));
        watcher.Poll();
        Assert.Empty(events);
    }
}

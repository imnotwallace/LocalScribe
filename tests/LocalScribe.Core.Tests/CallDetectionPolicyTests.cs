using LocalScribe.Core.Live;

public class CallDetectionPolicyTests
{
    // Design 2026-07-18 section 5.2. Pure function: every suppression branch is driven directly.

    private static readonly DateTimeOffset Now = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Defaults =
        ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"];

    private static CallAppActivity Started(string exe, int pid = 100)
        => new(exe, pid, CallAppActivityKind.Started, Now);

    private static CallDetectionSnapshot Snap(
        bool enabled = true, IReadOnlyList<string>? allowlist = null, int ownPid = 4242,
        bool recording = false, bool consoleArmed = false,
        IReadOnlyDictionary<string, DateTimeOffset>? lastOfferedAt = null, DateTimeOffset? now = null)
        => new(enabled, allowlist ?? Defaults, ownPid, recording, consoleArmed,
            lastOfferedAt ?? new Dictionary<string, DateTimeOffset>(), now ?? Now);

    [Fact]
    public void Allowlisted_start_offers_and_matching_ignores_case_and_extension()
    {
        // The scanner reports EXTENSIONLESS images (Process.ProcessName); the settings defaults
        // keep the design's ".exe" spelling - ExeKey folds both sides, both directions.
        Assert.True(CallDetectionPolicy.Decide(Started("CiscoCollabHost"), Snap()).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("WEBEX"), Snap()).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("zoom.EXE"), Snap()).Offer);
        Assert.False(CallDetectionPolicy.Decide(Started("chrome"), Snap()).Offer);   // browsers: excluded by default
        Assert.False(CallDetectionPolicy.Decide(Started("obs64"), Snap()).Offer);    // any non-listed mic user
    }

    [Fact]
    public void Stopped_events_never_offer()
    {
        var stopped = new CallAppActivity("webex", 100, CallAppActivityKind.Stopped, Now);
        var d = CallDetectionPolicy.Decide(stopped, Snap());
        Assert.False(d.Offer);
        Assert.NotNull(d.IgnoreReason);
    }

    [Fact]
    public void Master_toggle_off_ignores_everything()
    {
        Assert.False(CallDetectionPolicy.Decide(Started("webex"), Snap(enabled: false)).Offer);
    }

    [Fact]
    public void Own_process_is_always_excluded()
    {
        // LocalScribe's own mic capture is an active capture session too - it must never
        // self-offer (belt: the recording-active check also covers it; braces: pid).
        Assert.False(CallDetectionPolicy.Decide(Started("webex", pid: 4242), Snap(ownPid: 4242)).Offer);
    }

    [Fact]
    public void Active_session_or_armed_console_suppresses_offers()
    {
        Assert.False(CallDetectionPolicy.Decide(Started("webex"), Snap(recording: true)).Offer);
        Assert.False(CallDetectionPolicy.Decide(Started("webex"), Snap(consoleArmed: true)).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("webex"), Snap()).Offer);   // both clear -> offer
    }

    [Fact]
    public void Cooldown_suppresses_within_60s_and_reopens_at_the_boundary_per_exe()
    {
        var offered = new Dictionary<string, DateTimeOffset> { ["webex"] = Now };   // ExeKey form
        Assert.False(CallDetectionPolicy.Decide(Started("webex"),
            Snap(lastOfferedAt: offered, now: Now + TimeSpan.FromSeconds(59))).Offer);
        Assert.True(CallDetectionPolicy.Decide(Started("webex"),
            Snap(lastOfferedAt: offered, now: Now + TimeSpan.FromSeconds(60))).Offer);   // >= cooldown
        // Per-exe: another allowlisted app is not blocked by webex's ledger entry.
        Assert.True(CallDetectionPolicy.Decide(Started("Zoom"),
            Snap(lastOfferedAt: offered, now: Now + TimeSpan.FromSeconds(1))).Offer);
    }

    [Fact]
    public void ExeKey_trims_lowercases_and_strips_a_single_exe_suffix()
    {
        Assert.Equal("webex", CallDetectionPolicy.ExeKey("  WebEx.EXE "));
        Assert.Equal("ms-teams", CallDetectionPolicy.ExeKey("ms-teams"));
        Assert.Equal("ciscocollabhost", CallDetectionPolicy.ExeKey("CiscoCollabHost.exe"));
        Assert.Equal("app.exe", CallDetectionPolicy.ExeKey("app.exe.exe"));   // only the final suffix strips
    }

    [Fact]
    public void Every_ignore_carries_a_reason_and_offers_carry_none()
    {
        Assert.Null(CallDetectionPolicy.Decide(Started("webex"), Snap()).IgnoreReason);
        Assert.NotNull(CallDetectionPolicy.Decide(Started("chrome"), Snap()).IgnoreReason);
        Assert.NotNull(CallDetectionPolicy.Decide(Started("webex"), Snap(enabled: false)).IgnoreReason);
        Assert.NotNull(CallDetectionPolicy.Decide(Started("webex"), Snap(recording: true)).IgnoreReason);
    }
}

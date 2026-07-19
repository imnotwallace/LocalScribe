using LocalScribe.Core.Live;

public class CallEndAdvisorTests
{
    // Design 2026-07-18 section 5.4: recorded target's capture session goes inactive -> 3 s
    // debounce -> end-advisory DECISION (the toast + any stopping stay human actions in the App).
    // In-call software mute never fires this by the nature of the signal: the OS capture stream
    // stays open, so no Stopped event ever reaches Observe (verified in the Task 8 smoke).

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Defaults =
        ["CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe"];

    private static CallAppActivity Started(string exe, DateTimeOffset at)
        => new(exe, 100, CallAppActivityKind.Started, at);
    private static CallAppActivity Stopped(string exe, DateTimeOffset at)
        => new(exe, 100, CallAppActivityKind.Stopped, at);

    [Fact]
    public void Per_process_target_stop_advises_once_after_the_debounce()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm("CiscoCollabHost", new[] { "CiscoCollabHost" }, Defaults);
        advisor.Observe(Stopped("CiscoCollabHost", T0));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(2.9)));   // inside the window
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(3)));      // boundary: advise
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));    // one-shot per arm
    }

    [Fact]
    public void Session_return_within_the_window_cancels_and_a_later_stop_rearms()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm("webex", new[] { "webex" }, Defaults);
        advisor.Observe(Stopped("webex", T0));
        advisor.Observe(Started("webex", T0 + TimeSpan.FromSeconds(1)));      // brief blip, call continues
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));
        advisor.Observe(Stopped("webex", T0 + TimeSpan.FromSeconds(20)));     // the real hang-up
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(22)));
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(23)));
    }

    [Fact]
    public void Auto_arm_watches_only_allowlisted_active_exes()
    {
        // An Auto/system-mix session has no per-process target: watch the allowlisted apps that
        // were live at record start. A browser's capture session ending is not a call signal.
        var advisor = new CallEndAdvisor();
        advisor.Arm(null, new[] { "CiscoCollabHost", "chrome" }, Defaults);
        advisor.Observe(Stopped("chrome", T0));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(5)));     // not watched
        advisor.Observe(Stopped("CiscoCollabHost", T0 + TimeSpan.FromSeconds(6)));
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void All_watched_exes_must_be_quiet_before_the_clock_starts()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm(null, new[] { "CiscoCollabHost", "Zoom" }, Defaults);
        advisor.Observe(Stopped("Zoom", T0));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));    // Webex still live
        advisor.Observe(Stopped("CiscoCollabHost", T0 + TimeSpan.FromSeconds(12)));
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(14)));    // clock started at 12s
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void Exe_matching_strips_extension_and_case_between_settings_and_images()
    {
        // Arm with the SETTINGS spelling, observe the SCANNER spelling.
        var advisor = new CallEndAdvisor();
        advisor.Arm("webex.exe", Array.Empty<string>(), Defaults);
        advisor.Observe(Stopped("WEBEX", T0));
        Assert.True(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Disarm_clears_a_pending_window_and_empty_watch_never_advises()
    {
        var advisor = new CallEndAdvisor();
        advisor.Arm("Zoom", new[] { "Zoom" }, Defaults);
        advisor.Observe(Stopped("Zoom", T0));
        advisor.Disarm();                                                     // recording ended first
        Assert.False(advisor.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));

        var idle = new CallEndAdvisor();
        idle.Arm(null, new[] { "chrome" }, Defaults);                         // nothing allowlisted live
        idle.Observe(Stopped("chrome", T0));
        Assert.False(idle.ShouldAdvise(T0 + TimeSpan.FromSeconds(10)));       // honest silence
    }
}

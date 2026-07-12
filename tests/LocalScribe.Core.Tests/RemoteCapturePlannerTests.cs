using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class RemoteCapturePlannerTests
{
    private static readonly AudioSessionInfo Webex = new(4242, "CiscoCollabHost");
    private static readonly AudioSessionInfo Zoom = new(5151, "Zoom");
    private static readonly AudioSessionInfo Teams = new(6161, "ms-teams");
    private static readonly AudioSessionInfo Chrome = new(7171, "chrome");

    [Fact]
    public void Auto_prefers_webex_per_process_over_zoom()
    {
        var plan = RemoteCapturePlanner.Plan([Zoom, Webex], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.PerProcess, plan.Mode);
        Assert.Equal(4242u, plan.Pid);
        Assert.Equal("CiscoCollabHost", plan.App);
        Assert.False(plan.FellBackToSystemMix);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Auto_teams_falls_back_to_system_mix_with_notice()
    {
        var plan = RemoteCapturePlanner.Plan([Teams], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
        Assert.NotNull(plan.Notice);
        Assert.Equal("ms-teams", plan.App);
    }

    [Fact]
    public void Auto_browser_falls_back_to_system_mix()
    {
        var plan = RemoteCapturePlanner.Plan([Chrome], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
    }

    [Fact]
    public void Auto_no_active_sessions_falls_back_to_system_mix_with_notice()
    {
        var plan = RemoteCapturePlanner.Plan([], new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
        Assert.NotNull(plan.Notice);
        Assert.Null(plan.Pid);
    }

    [Fact]
    public void Explicit_per_process_matches_requested_app()
    {
        var plan = RemoteCapturePlanner.Plan([Zoom, Webex],
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" });
        Assert.Equal(RemoteMode.PerProcess, plan.Mode);
        Assert.Equal(5151u, plan.Pid);
    }

    [Fact]
    public void Explicit_per_process_on_known_all_zeros_app_STILL_falls_back()
    {
        // Spec 12.1: an explicit perProcess for the known all-zeros set still auto-falls-back -
        // a legal recording must never silently produce an empty remote.
        var plan = RemoteCapturePlanner.Plan([Teams],
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "ms-teams" });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
    }

    [Fact]
    public void Explicit_per_process_app_not_running_falls_back_with_notice()
    {
        var plan = RemoteCapturePlanner.Plan([Zoom],
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost" });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.True(plan.FellBackToSystemMix);
        Assert.NotNull(plan.Notice);
    }

    [Fact]
    public void Explicit_system_mix_is_chosen_not_a_fallback()
    {
        var plan = RemoteCapturePlanner.Plan([Webex], new RemoteSetting { Mode = RemoteMode.SystemMix });
        Assert.Equal(RemoteMode.SystemMix, plan.Mode);
        Assert.False(plan.FellBackToSystemMix);
        Assert.Null(plan.Notice);
    }

    [Fact]
    public void Matching_is_case_insensitive_substring()
    {
        var plan = RemoteCapturePlanner.Plan([new AudioSessionInfo(9, "ciscocollabhost")],
            new RemoteSetting { Mode = RemoteMode.Auto });
        Assert.Equal(RemoteMode.PerProcess, plan.Mode);
    }

    [Fact]
    public void KnownTargets_single_sources_the_friendly_fallbacks()
    {
        Assert.Contains(("Webex", "CiscoCollabHost"), RemoteCapturePlanner.KnownTargets);
        Assert.Contains(("Zoom", "Zoom"), RemoteCapturePlanner.KnownTargets);
    }

    [Fact]
    public void IsFullMix_flags_shared_audio_apps_only()
    {
        Assert.True(RemoteCapturePlanner.IsFullMix("chrome"));
        Assert.True(RemoteCapturePlanner.IsFullMix("ms-teams"));
        Assert.False(RemoteCapturePlanner.IsFullMix("CiscoCollabHost"));
        Assert.False(RemoteCapturePlanner.IsFullMix("Zoom"));
    }
}

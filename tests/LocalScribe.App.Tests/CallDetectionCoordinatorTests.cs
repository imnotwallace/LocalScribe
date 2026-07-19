using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CallDetectionCoordinatorTests
{
    // Design 2026-07-18 sections 5.2-5.4 glue: the coordinator assembles policy snapshots from
    // live seams, owns the per-exe offer ledger, and arms/disarms the call-end advisor. It raises
    // events ONLY - by construction it cannot start/stop/pause/gate/mark anything (locked rule).

    private static readonly DateTimeOffset T0 = new(2026, 7, 19, 10, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public CallDetectSetting Setting = new();
        public bool Recording;
        public bool ConsoleArmed;
        public readonly ManualUtcTimeProvider Time = new(T0);
        public readonly List<string> Offers = new();
        public int EndAdvised;
        public readonly CallDetectionCoordinator Coordinator;

        public Harness()
        {
            Coordinator = new CallDetectionCoordinator(() => Setting, () => Recording,
                () => ConsoleArmed, ownPid: 4242, Time);
            Coordinator.OfferRequested += Offers.Add;
            Coordinator.CallEndAdvised += () => EndAdvised++;
        }
    }

    private static CallAppActivity Started(string exe, int pid, DateTimeOffset at)
        => new(exe, pid, CallAppActivityKind.Started, at);
    private static CallAppActivity Stopped(string exe, int pid, DateTimeOffset at)
        => new(exe, pid, CallAppActivityKind.Stopped, at);

    [Fact]
    public void Allowlisted_start_offers_once_then_the_ledger_cooldown_suppresses()
    {
        var h = new Harness();
        h.Coordinator.OnActivity(Started("CiscoCollabHost", 100, T0));
        Assert.Equal(new[] { "CiscoCollabHost" }, h.Offers);

        h.Time.Set(T0 + TimeSpan.FromSeconds(30));
        h.Coordinator.OnActivity(Started("CiscoCollabHost", 101, h.Time.GetUtcNow()));   // new pid, same exe
        Assert.Single(h.Offers);                                    // inside the 60 s cooldown

        h.Time.Set(T0 + TimeSpan.FromSeconds(61));
        h.Coordinator.OnActivity(Started("CiscoCollabHost", 102, h.Time.GetUtcNow()));
        Assert.Equal(2, h.Offers.Count);                            // cooldown elapsed: re-offer
    }

    [Fact]
    public void Recording_console_armed_and_toggle_off_all_suppress_offers()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        h.Recording = false;
        h.ConsoleArmed = true;
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        h.ConsoleArmed = false;
        h.Setting = h.Setting with { Enabled = false };
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        Assert.Empty(h.Offers);

        h.Setting = h.Setting with { Enabled = true };              // live toggle: next event offers
        h.Coordinator.OnActivity(Started("webex", 100, T0));
        Assert.Equal(new[] { "webex" }, h.Offers);
    }

    [Fact]
    public void Own_pid_never_offers()
    {
        var h = new Harness();
        h.Coordinator.OnActivity(Started("webex", 4242, T0));       // our own capture session
        Assert.Empty(h.Offers);
    }

    [Fact]
    public void End_advice_fires_once_after_the_debounce_only_while_recording()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnRecordingStarted("CiscoCollabHost", Array.Empty<string>());
        h.Coordinator.OnActivity(Stopped("CiscoCollabHost", 100, T0));
        h.Time.Set(T0 + TimeSpan.FromSeconds(1.5));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);                              // window still open
        h.Time.Set(T0 + TimeSpan.FromSeconds(3));
        h.Coordinator.OnTick();
        Assert.Equal(1, h.EndAdvised);                              // debounce elapsed
        h.Coordinator.OnTick();
        Assert.Equal(1, h.EndAdvised);                              // one-shot per arm
    }

    [Fact]
    public void Session_return_cancels_and_recording_stop_disarms()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnRecordingStarted("webex", Array.Empty<string>());
        h.Coordinator.OnActivity(Stopped("webex", 100, T0));
        h.Coordinator.OnActivity(Started("webex", 100, T0 + TimeSpan.FromSeconds(1)));   // blip
        h.Time.Set(T0 + TimeSpan.FromSeconds(10));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);

        h.Coordinator.OnActivity(Stopped("webex", 100, h.Time.GetUtcNow()));
        h.Recording = false;
        h.Coordinator.OnRecordingStopped();                          // user stopped first
        h.Time.Set(T0 + TimeSpan.FromSeconds(20));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);                               // disarmed: nothing pending
    }

    [Fact]
    public void Auto_mode_arms_from_the_allowlisted_active_exes()
    {
        var h = new Harness();
        h.Recording = true;
        h.Coordinator.OnRecordingStarted(null, new[] { "CiscoCollabHost", "chrome" });
        h.Coordinator.OnActivity(Stopped("chrome", 300, T0));        // not allowlisted: not watched
        h.Time.Set(T0 + TimeSpan.FromSeconds(5));
        h.Coordinator.OnTick();
        Assert.Equal(0, h.EndAdvised);
        h.Coordinator.OnActivity(Stopped("CiscoCollabHost", 100, h.Time.GetUtcNow()));
        h.Time.Set(T0 + TimeSpan.FromSeconds(8));
        h.Coordinator.OnTick();
        Assert.Equal(1, h.EndAdvised);
    }
}

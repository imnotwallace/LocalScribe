using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CompactConsoleViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-compact-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (CompactConsoleViewModel Compact, SessionViewModel Session, TranscriptLinesViewModel Lines)
        MakeVms(SessionController controller, bool compactOnStart)
    {
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var settings = new FakeSettingsService(new Settings
        { Console = new ConsoleSetting { CompactOnStart = compactOnStart } });
        var lines = new TranscriptLinesViewModel(controller, settings, a => a());
        return (new CompactConsoleViewModel(session, lines, settings), session, lines);
    }

    [Theory]
    [InlineData(SessionState.Idle, SessionState.Recording, false, true, true)]        // opt-in auto-compact
    [InlineData(SessionState.Idle, SessionState.Recording, false, false, false)]      // default OFF stays full
    [InlineData(SessionState.Recording, SessionState.Paused, true, false, true)]      // pause keeps the pill
    [InlineData(SessionState.Paused, SessionState.Recording, true, false, true)]      // resume keeps the pill
    [InlineData(SessionState.Recording, SessionState.Finalizing, true, true, false)]  // stop restores full...
    [InlineData(SessionState.Finalizing, SessionState.Idle, true, true, false)]       // ...and idle stays full
    [InlineData(SessionState.Recording, SessionState.Recording, true, false, true)]   // no transition: unchanged
    public void NextCompact_encodes_the_whole_transition_rule(
        SessionState prev, SessionState next, bool current, bool optIn, bool expected)
        => Assert.Equal(expected, CompactConsoleViewModel.NextCompact(prev, next, current, optIn));

    [Fact]
    public void MutePill_maps_states_with_the_app_mute_advisory_winning_first()
    {
        // Design 2026-07-18 section 6 (locked): advisory banners collapse to a colored pill state
        // plus a tooltip - NEVER lost. The full console renders the app-mute advisory banner
        // UNCONDITIONALLY of IsLocalMuted (LiveViewWindow.xaml gates the Grid's Visibility on
        // Session.AppMuteBannerVisible alone), so the pill must surface it over the generic mute
        // states too - checking localMuted first would mask AppLiveButMuted, which
        // AppMuteBannerEvaluator only ever raises while localMuted==true, permanently.
        Assert.Equal((CompactMuteState.Normal, "Mute my side (Ctrl+Shift+M)"),
            CompactConsoleViewModel.MutePill(false, false, AppMuteBannerKind.None, ""));

        var muted = CompactConsoleViewModel.MutePill(true, false, AppMuteBannerKind.None, "");
        Assert.Equal(CompactMuteState.Muted, muted.State);
        Assert.Contains("not being recorded", muted.Tooltip);

        var device = CompactConsoleViewModel.MutePill(false, true, AppMuteBannerKind.None, "");
        Assert.Equal(CompactMuteState.DeviceMuted, device.State);
        Assert.Contains("microphone device is muted", device.Tooltip);

        var advisory = CompactConsoleViewModel.MutePill(false, false,
            AppMuteBannerKind.AppMutedButRecording,
            "Webex looks muted - LocalScribe is still recording your side.");
        Assert.Equal(CompactMuteState.AppMuteAdvisory, advisory.State);
        Assert.Equal("Webex looks muted - LocalScribe is still recording your side.",
            advisory.Tooltip);                                   // the exact banner text, never lost

        // Load-bearing: this is the realistic co-occurrence (AppLiveButMuted fires only while
        // localMuted==true) the whole-branch review flagged - mute-first (the old, buggy order)
        // would return Muted here; advisory-first (the fix) returns AppMuteAdvisory with the
        // banner's EXACT text, so this genuinely distinguishes the two orderings, not just the
        // presence of the advisory case.
        var liveButMuted = CompactConsoleViewModel.MutePill(true, false,
            AppMuteBannerKind.AppLiveButMuted,
            "You are unmuted in Webex - LocalScribe is not recording your side.");
        Assert.Equal(CompactMuteState.AppMuteAdvisory, liveButMuted.State);
        Assert.Equal("You are unmuted in Webex - LocalScribe is not recording your side.",
            liveButMuted.Tooltip);                                // NOT the generic Muted tooltip

        // Same co-occurrence with the device-mute fact also present: the advisory still wins over
        // BOTH generic states. (This pair was the mis-pinned assertion - previously expected
        // Muted/DeviceMuted, which encoded the masking bug this change fixes.)
        Assert.Equal(CompactMuteState.AppMuteAdvisory,
            CompactConsoleViewModel.MutePill(true, true, AppMuteBannerKind.AppLiveButMuted, "x").State);
        Assert.Equal(CompactMuteState.AppMuteAdvisory,
            CompactConsoleViewModel.MutePill(false, true, AppMuteBannerKind.AppLiveButMuted, "x").State);
    }

    [Fact]
    public void PillLine_renders_a_single_end_trimmed_line()
    {
        Assert.Equal(CompactConsoleViewModel.ListeningText,
            CompactConsoleViewModel.PillLine(null, listening: true));       // warm-up carried into the pill
        Assert.Equal("", CompactConsoleViewModel.PillLine(null, listening: false));
        Assert.Equal("Sam: Hello there",
            CompactConsoleViewModel.PillLine(new TranscriptLineViewModel("00:05", "Sam", "Hello there  ", false), false));
        // Markers carry no speaker in the line VM - text only (verbatim; markers surface too).
        Assert.Equal("recording paused",
            CompactConsoleViewModel.PillLine(new TranscriptLineViewModel("00:09", "", "recording paused", true), false));
        // Any embedded newline collapses: the pill is a SINGLE line, end-trimmed (layout only).
        Assert.Equal("Sam: line one line two",
            CompactConsoleViewModel.PillLine(new TranscriptLineViewModel("00:12", "Sam", "line one\r\nline two\n", false), false));
    }

    [Fact]
    public async Task Auto_compact_on_start_honors_the_setting_and_stop_restores_full()
    {
        // GatedEngineFactory holds the engine build closed so no transcript line can land - the
        // listening fallback (the pill's warm-up surface) is observable and deterministic.
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var (compact, session, _) = MakeVms(controller, compactOnStart: true);
        Assert.False(compact.IsCompact);                          // idle: always the full console

        await session.StartCommand.ExecuteAsync(null);
        Assert.True(compact.IsCompact);                           // opt-in honored on Idle->Recording
        Assert.Equal(CompactConsoleViewModel.ListeningText, compact.LastLineText);

        gated.CreateGate.Set();                                   // release the engine before pausing/stopping
        await session.PauseResumeCommand.ExecuteAsync(null);      // pause
        Assert.True(compact.IsCompact);                           // pause keeps the pill
        await session.StopCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);                          // stop from the pill restores the full console
        session.Dispose(); compact.Dispose();
    }

    [Fact]
    public async Task Default_off_start_stays_full_and_the_toggle_flips_both_ways()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var (compact, session, _) = MakeVms(controller, compactOnStart: false);
        await session.StartCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);                          // DEFAULT OFF (design section 6)

        compact.ToggleCompactCommand.Execute(null);               // header Compact button
        Assert.True(compact.IsCompact);
        compact.ToggleCompactCommand.Execute(null);               // pill Expand button
        Assert.False(compact.IsCompact);

        compact.ToggleCompactCommand.Execute(null);
        await session.StopCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);                          // stop ALWAYS restores the full console
        session.Dispose(); compact.Dispose();
    }

    [Fact]
    public async Task Constructed_mid_recording_applies_the_auto_compact_choice()
    {
        // Production timing: the app opens the console ON Idle->Recording, so the hide-on-close
        // singleton window (and this VM) can be constructed AFTER the transition already happened -
        // the ctor must evaluate auto-compact against the CURRENT state, not only future events.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await session.StartCommand.ExecuteAsync(null);

        var settings = new FakeSettingsService(new Settings
        { Console = new ConsoleSetting { CompactOnStart = true } });
        var lines = new TranscriptLinesViewModel(controller, settings, a => a());
        var compact = new CompactConsoleViewModel(session, lines, settings);
        Assert.True(compact.IsCompact);

        await session.StopCommand.ExecuteAsync(null);
        Assert.False(compact.IsCompact);
        session.Dispose(); compact.Dispose();
    }

    [Fact]
    public void Last_line_follows_the_live_list()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var (compact, _, lines) = MakeVms(controller, compactOnStart: false);
        Assert.Equal("", compact.LastLineText);                   // idle + empty: no hint, no line

        lines.Lines.Add(new TranscriptLineViewModel("00:05", "Sam", "first line", false));
        Assert.Equal("Sam: first line", compact.LastLineText);
        lines.Lines.Add(new TranscriptLineViewModel("00:09", "", "recording paused", true));
        Assert.Equal("recording paused", compact.LastLineText);   // markers surface too (verbatim rule)
    }

    [Fact]
    public async Task Mute_pill_follows_the_sessions_mute_state()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var (compact, session, _) = MakeVms(controller, compactOnStart: false);
        Assert.Equal(CompactMuteState.Normal, compact.MuteState);

        await session.StartCommand.ExecuteAsync(null);
        await session.MuteLocalCommand.ExecuteAsync(null);
        // LocalMuteChanged marshals through the synchronous dispatch, but the controller raises it
        // from the mute call's own continuation - bound the wait on the observable effect.
        Assert.True(SpinWait.SpinUntil(() => compact.MuteState == CompactMuteState.Muted, TimeSpan.FromSeconds(2)),
            "mute never reached the compact pill");
        Assert.Contains("unmute", compact.MuteTooltip, StringComparison.OrdinalIgnoreCase);

        await session.MuteLocalCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => compact.MuteState == CompactMuteState.Normal, TimeSpan.FromSeconds(2)),
            "unmute never reached the compact pill");
        await session.StopCommand.ExecuteAsync(null);
        session.Dispose(); compact.Dispose();
    }
}

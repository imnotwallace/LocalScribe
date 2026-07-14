using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 Phase 3 C1: the Record console's idle-state VM. It derives the capture
/// summary from Settings (what Start WILL do - no live WASAPI probe, locked decision 3) and
/// owns the per-session Remote-target picker, which mirrors into RemoteTargetOverride (never
/// into settings.json). All lifecycle state/commands stay on the shared SessionViewModel
/// (locked decision 1: no new lifecycle logic; this VM only composes it). WPF-free;
/// settings.Changed carries no thread contract, so its handler marshals through the injected
/// dispatch.
/// Task 6 (design 2026-07-12 sections 1 & 4) replaces the old free-text app box with
/// RemoteTargetOptions: Auto, live apps (friendly-labelled via a FakeScanner, FullMix
/// annotated), the pinned Webex/Zoom fallbacks, and System mix - selection mirrors into
/// RemoteTargetOverride and a live pick hot-swaps through SessionViewModel.SwitchRemoteTargetAsync
/// under a confirm gate for System mix.
/// Stage 6.2 Task 7 adds an optional multi-select matter picker: ticking a matter writes
/// MatterSelectionOverride.MatterIds (mirrors RemoteTargetOverride - per-session, never persisted
/// to settings.json), and SessionViewModel reads the seam at Start to bias the Whisper prompt +
/// seed meta.MatterIds. Ending a session (Idle) clears the picks, same as the target picker.</summary>
public sealed class RecordingConsoleViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-console-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Settings PerProcess(string? app) => new()
    { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = app } };

    private static Settings Auto(string? app) => new()
    { Remote = new RemoteSetting { Mode = RemoteMode.Auto, App = app } };

    private static Settings SystemMix() => new()
    { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix } };

    private readonly FakeCaptureDeviceEnumerator _devices =
        new(new LocalScribe.Core.Live.AudioDeviceInfo("id-headset", "Headset Microphone"),
            new LocalScribe.Core.Live.AudioDeviceInfo("id-webcam", "Webcam Mic"));

    private sealed class FakeScanner : IAudioSessionScanner
    {
        public List<AudioSessionInfo> Active = new();
        public IReadOnlyList<AudioSessionInfo> Scan() => Active;
    }

    private readonly FakeScanner _scanner = new();

    private (RecordingConsoleViewModel Console, FakeSettingsService Settings,
        SessionViewModel Session, RemoteTargetOverride Override, MaintenanceService Maintenance,
        MatterSelectionOverride MatterSelection, MicOverride Mic) MakeConsole(
            Settings? initial = null, Func<bool>? confirmSystemMix = null)
    {
        var settings = new FakeSettingsService(initial ?? PerProcess("Webex"));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var over = new RemoteTargetOverride();
        var maintenance = new MaintenanceService(new StoragePaths(_root), settings,
            new FakeRecycleBin(), TimeProvider.System);
        var matterSelection = new MatterSelectionOverride();
        var micOverride = new MicOverride();
        var console = new RecordingConsoleViewModel(settings, session, over, maintenance,
            matterSelection, _devices, micOverride, _scanner, confirmSystemMix ?? (() => true),
            dispatch: a => a());
        return (console, settings, session, over, maintenance, matterSelection, micOverride);
    }

    // Picker tests need at least one non-archived matter in the catalog; this drives that
    // through the same MaintenanceService the console reads via LoadMattersAsync.
    private async Task<(RecordingConsoleViewModel Vm, MatterSelectionOverride Seam,
        SessionViewModel Session, MaintenanceService Maintenance)> MakeWithOneMatterAsync()
    {
        var (console, _, session, _, maintenance, seam, _) = MakeConsole();
        await maintenance.CreateMatterAsync("Doe v. State", CancellationToken.None);
        await console.LoadMattersAsync();
        return (console, seam, session, maintenance);
    }

    [Fact]
    public void Base_options_always_include_auto_fallbacks_and_system_mix()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        Assert.Contains(console.RemoteTargetOptions, o => o.Setting.Mode == RemoteMode.Auto);
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "Webex" && o.Setting.App == "CiscoCollabHost");
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "Zoom" && o.Setting.App == "Zoom");
        Assert.Contains(console.RemoteTargetOptions, o => o.IsSystemMix);
    }

    [Fact]
    public void Seeds_selection_and_override_from_settings()
    {
        var (auto, _, _, autoOver, _, _, _) = MakeConsole(Auto(null));
        Assert.Equal(RemoteMode.Auto, auto.SelectedRemoteTarget.Setting.Mode);
        Assert.Null(autoOver.Override);                                  // untouched Auto -> follows settings

        var (per, _, _, perOver, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", per.SelectedRemoteTarget.Setting.App);
        Assert.Equal("Webex", perOver.Override?.App);
    }

    [Fact]
    public void Picking_an_option_mirrors_into_the_override()
    {
        var (console, settings, _, over, _, _, _) = MakeConsole(Auto(null));
        var zoom = console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom");
        console.SelectedRemoteTarget = zoom;
        Assert.Equal(RemoteMode.PerProcess, over.Override?.Mode);
        Assert.Equal("Zoom", over.Override?.App);
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task Refresh_builds_friendly_labels_dedups_by_image_and_annotates_fullmix()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        _scanner.Active.Add(new AudioSessionInfo(1, "CiscoCollabHost"));  // live Webex
        _scanner.Active.Add(new AudioSessionInfo(2, "chrome"));           // FullMix
        await console.RefreshRemoteTargetsAsync();

        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "CiscoCollabHost - Webex");
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "chrome (captured as system mix)");
        // Webex fallback (image CiscoCollabHost) is deduped away by the live CiscoCollabHost entry.
        Assert.DoesNotContain(console.RemoteTargetOptions,
            o => o.Label == "Webex" && o.Setting.App == "CiscoCollabHost");
        Assert.Contains(console.RemoteTargetOptions, o => o.Label == "Zoom");   // Zoom fallback still pinned
    }

    [Fact]
    public async Task Live_pick_of_an_app_hot_swaps_and_updates_the_override()
    {
        var (console, _, session, over, _, _, _) = MakeConsole(Auto(null));
        await session.StartCommand.ExecuteAsync(null);
        var zoom = console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom");
        await console.ChangeRemoteTargetCommand.ExecuteAsync(zoom);
        Assert.Equal("Zoom", over.Override?.App);
        Assert.Equal("Zoom", console.SelectedRemoteTarget.Setting.App);
        await session.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Live_switch_to_system_mix_is_gated_by_confirm()
    {
        var (console, _, session, over, _, _, _) = MakeConsole(Auto(null), confirmSystemMix: () => false);
        await session.StartCommand.ExecuteAsync(null);
        var before = console.SelectedRemoteTarget;
        var mix = console.RemoteTargetOptions.First(o => o.IsSystemMix);
        await console.ChangeRemoteTargetCommand.ExecuteAsync(mix);
        Assert.Equal(before, console.SelectedRemoteTarget);   // declined -> selection unchanged
        Assert.NotEqual(RemoteMode.SystemMix, over.Override?.Mode ?? RemoteMode.Auto);
        await session.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Live_app_switch_commits_the_selected_target()
    {
        // Happy path only: a live app pick reaches the controller and commits. The build-FAILURE
        // revert path is NOT asserted here (FakeProvider's throw seam is not surfaced through
        // MakeConsole); it is covered deterministically in Task 5's provider-visible controller test.
        var (console, _, session, _, _, _, _) = MakeConsole(Auto(null));
        await session.StartCommand.ExecuteAsync(null);
        await console.ChangeRemoteTargetCommand.ExecuteAsync(console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom"));
        Assert.Equal("Zoom", console.SelectedRemoteTarget.Setting.App);   // normal pick commits
        await session.StopCommand.ExecuteAsync(null);
    }

    // --- Coverage-gap (plan self-review (d), user-flagged): the old free-text OnSettingsChanged
    // reseed tests (Settings_change_reseeds_an_untouched_selector /
    // Settings_change_keeps_a_user_diverged_selector / Switching_to_system_mix_clears_a_diverged_
    // app_override) referenced the deleted SessionTargetApp and were dropped by the picker
    // rewrite. These three are their picker-equivalents, exercising OnSettingsChanged's reseed
    // condition (newSettings.Remote.Mode == SystemMix || _selectedRemoteTarget == OptionFor(oldSettings.Remote)).

    [Fact]
    public async Task Untouched_selection_follows_a_settings_change()
    {
        var (console, settings, _, _, _, _, _) = MakeConsole(Auto(null));
        // untouched: SelectedRemoteTarget is still the Auto option seeded at construction.

        await settings.SaveAsync(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" } }, CancellationToken.None);

        Assert.Equal("Zoom", console.SelectedRemoteTarget.Setting.App);   // untouched selection followed the new default
    }

    [Fact]
    public async Task A_user_diverged_selection_is_preserved_across_a_settings_change()
    {
        var (console, settings, _, _, _, _, _) = MakeConsole(Auto(null));
        console.SelectedRemoteTarget = console.RemoteTargetOptions.First(o => o.Setting.App == "CiscoCollabHost");   // Webex fallback

        await settings.SaveAsync(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" } }, CancellationToken.None);

        // A background default change must not clobber the user's in-flight per-session pick.
        Assert.Equal("CiscoCollabHost", console.SelectedRemoteTarget.Setting.App);
    }

    [Fact]
    public async Task Switching_the_base_to_system_mix_clears_an_armed_app_pick()
    {
        var (console, settings, _, over, _, _, _) = MakeConsole(PerProcess("Webex"));   // override armed with a per-app pick
        Assert.NotNull(over.Override);

        await settings.SaveAsync(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix } }, CancellationToken.None);

        // Mirrors the old free-text selector forcing "" on SystemMix: any armed app override
        // must be dropped, since SystemMix has no per-app target.
        Assert.True(console.SelectedRemoteTarget.IsSystemMix);
        Assert.Null(over.Override);
    }

    [Fact]
    public void A_pin_to_a_present_device_resolves_SelectedMic_to_it()
    {
        // B1-2: the RemoteSummary/MicSummary display tests were removed with the dead properties;
        // the underlying remote-target selection is covered by the mirror tests above, and the
        // follow-default fallback by the ghost-mic test below. The one piece of unique coverage kept
        // here (asserting the real state rather than the removed summary string): a pin whose device
        // IS present resolves SelectedMic to that device (as opposed to the absent-device fallback).
        var (pinned, _, _, _, _, _, _) = MakeConsole(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" } });
        Assert.Equal("id-headset", pinned.SelectedMic.Id);
        Assert.Equal("Headset Microphone", pinned.SelectedMic.Name);
    }

    [Fact]
    public async Task Dispose_unsubscribes_settings_and_session()
    {
        var (console, settings, session, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", console.SelectedRemoteTarget.Setting.App);      // untouched selector

        console.Dispose();
        console.Dispose();                                    // idempotent - must not throw
        var raised = new List<string>();
        console.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        // Settings leg: an UNTOUCHED selector would re-seed if still subscribed (see
        // Untouched_selection_follows_a_settings_change) - after Dispose it must not.
        await settings.SaveAsync(PerProcess("CiscoCollabHost"), CancellationToken.None);
        Assert.Equal("Webex", console.SelectedRemoteTarget.Setting.App);
        Assert.Equal("Webex", over.Override?.App);

        // Session leg: a return to Idle would re-seed from Current ("CiscoCollabHost" now)
        // if still subscribed - after Dispose it must not. State's setter is public
        // (generated by [ObservableProperty]); MetadataEditorViewModelTests'
        // Dispose_unsubscribes test drives it the same way.
        session.State = SessionState.Recording;
        session.State = SessionState.Idle;
        Assert.Equal("Webex", console.SelectedRemoteTarget.Setting.App);

        Assert.Empty(raised);
    }

    [Fact]
    public async Task Toggling_a_matter_updates_the_selection_seam()
    {
        var (vm, seam, _, _) = await MakeWithOneMatterAsync();
        var option = Assert.Single(vm.MatterOptions);
        vm.ToggleMatterCommand.Execute(option);

        Assert.True(vm.MatterOptions[0].IsSelected);
        Assert.Single(seam.MatterIds);
        Assert.Equal(option.Id, seam.MatterIds[0]);
    }

    [Fact]
    public async Task Ending_a_session_clears_the_selection()
    {
        var (vm, seam, session, _) = await MakeWithOneMatterAsync();
        vm.ToggleMatterCommand.Execute(vm.MatterOptions[0]);
        Assert.NotEmpty(seam.MatterIds);

        // Drive the real lifecycle: Start -> Recording, Stop -> Idle. The console's OnSessionChanged
        // (the same handler that reverts the target picker) clears the picks on Idle.
        await session.StartCommand.ExecuteAsync(null);
        await session.StopCommand.ExecuteAsync(null);

        Assert.Empty(seam.MatterIds);
        Assert.All(vm.MatterOptions, o => Assert.False(o.IsSelected));
    }

    [Fact]
    public async Task Search_filters_the_options()
    {
        var (vm, _, _, _) = await MakeWithOneMatterAsync();
        vm.MatterPickerQuery = "zzz-no-match";
        Assert.Empty(vm.MatterOptions);
        vm.MatterPickerQuery = "Doe";
        Assert.Single(vm.MatterOptions);
    }

    // --- Review fix (Task 7): closes the "picker only ever loads once" staleness gap and the
    // missing !Archived coverage. Both are fully deterministic - direct await LoadMattersAsync(),
    // no window hook, no timing.

    [Fact]
    public async Task Reloading_picks_up_a_newly_created_matter()
    {
        var (vm, _, _, maintenance) = await MakeWithOneMatterAsync();
        Assert.Single(vm.MatterOptions);

        await maintenance.CreateMatterAsync("Roe v. Wade", CancellationToken.None);
        await vm.LoadMattersAsync();

        Assert.Equal(2, vm.MatterOptions.Count);
    }

    [Fact]
    public async Task Reload_drops_a_pick_whose_matter_was_deleted()
    {
        var (vm, seam, _, maintenance) = await MakeWithOneMatterAsync();
        var option = Assert.Single(vm.MatterOptions);
        vm.ToggleMatterCommand.Execute(option);
        Assert.Single(seam.MatterIds);                        // the pick took

        await maintenance.DeleteMatterAsync(option.Id, CancellationToken.None);
        await vm.LoadMattersAsync();

        Assert.Empty(vm.MatterOptions);                        // catalog reloaded without it
        Assert.Empty(seam.MatterIds);                          // dangling pick reconciled out
        Assert.Equal("No matters selected (record first, classify later).",
            vm.SelectedMatterSummary);
    }

    [Fact]
    public async Task LoadMattersAsync_excludes_archived_matters()
    {
        var (console, _, _, _, maintenance, _, _) = MakeConsole();
        var kept = await maintenance.CreateMatterAsync("Doe v. State", CancellationToken.None);
        var archived = await maintenance.CreateMatterAsync("Old Matter", CancellationToken.None);
        await maintenance.SaveMatterAsync(archived with { Archived = true }, CancellationToken.None);

        await console.LoadMattersAsync();

        var option = Assert.Single(console.MatterOptions);
        Assert.Equal(kept.Id, option.Id);
    }

    [Fact]
    public void Selecting_a_console_mic_sets_the_override()
    {
        var (console, _, _, _, _, _, mic) = MakeConsole();
        console.SelectedMic = console.MicChoices.First(c => c.Id == "id-webcam");
        Assert.NotNull(mic.Override);
        Assert.Equal(MicMode.Pinned, mic.Override!.Mode);
        Assert.Equal("id-webcam", mic.Override.Id);
    }

    [Fact]
    public void Selecting_follow_default_overrides_a_saved_pin_back_to_default()
    {
        var (console, _, _, _, _, _, mic) = MakeConsole(new Settings
        {
            Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" },
            Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" },
        });
        console.SelectedMic = console.MicChoices.First(c => c.Id is null);
        Assert.NotNull(mic.Override);
        Assert.Equal(MicMode.FollowDefault, mic.Override!.Mode);
    }

    [Fact]
    public async Task Ending_a_session_clears_the_mic_override()
    {
        var (console, _, session, _, _, _, mic) = MakeConsole();
        console.SelectedMic = console.MicChoices.First(c => c.Id == "id-webcam");
        Assert.NotNull(mic.Override);

        // Mirrors Session_stop_reseeds_the_selector_to_the_saved_default's mechanism for
        // reaching Idle: drive the real lifecycle via Start/Stop, not a test-only setter.
        await session.StartCommand.ExecuteAsync(null);
        await session.StopCommand.ExecuteAsync(null);

        Assert.Null(mic.Override);
        Assert.Equal(console.MicChoices[0], console.SelectedMic);   // Idle re-seeded from Settings (follow-default)
    }

    // --- Final-review fix wave: the applied mic selection must derive from the APPLIED plan
    // (override + selection), never the base settings, so the console can never disagree with
    // what capture will actually do.

    [Fact]
    public void Console_mic_selection_follows_dropdown_for_absent_pin()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-not-present", Name = "Ghost Mic" } });

        Assert.Null(console.SelectedMic.Id);                             // absent pin -> follow-default
    }

    [Fact]
    public async Task Settings_pin_change_reseeds_console_dropdown_when_no_override()
    {
        var (console, settings, _, _, _, _, _) = MakeConsole();

        await settings.SaveAsync(new Settings
        {
            Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-webcam", Name = "Webcam Mic" },
        }, CancellationToken.None);

        Assert.Equal("id-webcam", console.SelectedMic.Id);
    }

    [Fact]
    public void PreflightLine_maps_planner_outcomes_to_ready_card_text()
    {
        // Design 2026-07-13 section 5 item 5: the line is derived from the SAME pure
        // RemoteCapturePlanner Start resolves through, so it never lies about the plan.
        var auto = new RemoteSetting { Mode = RemoteMode.Auto };
        var none = new List<AudioSessionInfo>();

        Assert.Equal("Webex detected - remote audio will be captured from it.",
            RecordingConsoleViewModel.PreflightLine(
                new List<AudioSessionInfo> { new(1, "CiscoCollabHost") }, auto));

        Assert.Equal("No call app playing audio - will record system mix.",
            RecordingConsoleViewModel.PreflightLine(none, auto));

        // A LIVE full-mix app (Teams) is detected but honestly reported as system-mix capture.
        Assert.Equal("Teams detected - will record system mix (shared-audio app).",
            RecordingConsoleViewModel.PreflightLine(
                new List<AudioSessionInfo> { new(2, "ms-teams") }, auto));

        // A pinned full-mix app that IS live reports the same honest degrade...
        Assert.Equal("Browser detected - will record system mix (shared-audio app).",
            RecordingConsoleViewModel.PreflightLine(
                new List<AudioSessionInfo> { new(3, "chrome") },
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = "chrome" }));

        // ...but a pinned app that is NOT live must not claim detection (planner fallback keeps
        // plan.App = the requested image, so the helper checks live-ness before saying "detected").
        Assert.Equal("No call app playing audio - will record system mix.",
            RecordingConsoleViewModel.PreflightLine(none,
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = "chrome" }));

        Assert.Equal("System mix - all system audio will be recorded.",
            RecordingConsoleViewModel.PreflightLine(none, new RemoteSetting { Mode = RemoteMode.SystemMix }));
    }

    [Fact]
    public async Task Preflight_and_engine_chip_populate_on_refresh_and_follow_the_picker()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        Assert.Equal("", console.PreflightSummary);
        Assert.Equal("", console.EngineSummary);

        _scanner.Active.Add(new AudioSessionInfo(1, "CiscoCollabHost"));
        await console.RefreshRemoteTargetsAsync();
        Assert.Equal("Webex detected - remote audio will be captured from it.", console.PreflightSummary);
        // MakeConsole's controller: StaticHardwareProbe -> Cpu; Model=auto over {base.en,tiny.en}.
        Assert.Equal("base.en \u00B7 CPU", console.EngineSummary);

        // The line follows the per-session picker (the APPLIED remote setting, not raw settings).
        _scanner.Active.Add(new AudioSessionInfo(2, "Zoom"));
        var zoom = console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom");
        console.SelectedRemoteTarget = zoom;
        await console.RefreshRemoteTargetsAsync();
        Assert.Equal("Zoom detected - remote audio will be captured from it.", console.PreflightSummary);

        _scanner.Active.Clear();
        await console.RefreshRemoteTargetsAsync();       // pinned Zoom no longer live -> honest fallback
        Assert.Equal("No call app playing audio - will record system mix.", console.PreflightSummary);
    }
}

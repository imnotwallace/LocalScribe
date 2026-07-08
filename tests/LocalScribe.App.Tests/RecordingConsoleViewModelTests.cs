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
/// owns the per-session target-app selector, which mirrors into RemoteAppOverride (trimmed,
/// empty -> null) and NEVER writes settings.json. Harness: real SessionViewModel over
/// LiveTestDoubles.MakeController (the SessionViewModelTests pattern), synchronous
/// FakeSettingsService, dispatch a => a() so assertions stay synchronous.
/// Stage 6.2 Task 7 adds the matter picker: a real MaintenanceService over a temp root backs
/// MatterOptions, and MatterSelectionOverride is the seam the picker writes (mirrors
/// RemoteAppOverride, cleared on Idle - never persisted to settings.json).</summary>
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

    private (RecordingConsoleViewModel Console, FakeSettingsService Settings,
        SessionViewModel Session, RemoteAppOverride Override, MaintenanceService Maintenance,
        MatterSelectionOverride MatterSelection, MicOverride Mic) MakeConsole(Settings? initial = null)
    {
        var settings = new FakeSettingsService(initial ?? PerProcess("Webex"));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());      // test VAD, preflight off
        var over = new RemoteAppOverride();
        var maintenance = new MaintenanceService(new StoragePaths(_root), settings,
            new FakeRecycleBin(), TimeProvider.System);
        var matterSelection = new MatterSelectionOverride();
        var micOverride = new MicOverride();
        var console = new RecordingConsoleViewModel(settings, session, over, maintenance,
            matterSelection, _devices, micOverride, dispatch: a => a());
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
    public void Seeds_selector_and_override_from_settings_at_construction()
    {
        var (console, _, _, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", console.SessionTargetApp);
        Assert.Equal("Webex", over.App);

        var (empty, _, _, emptyOver, _, _, _) = MakeConsole(PerProcess(null));
        Assert.Equal("", empty.SessionTargetApp);
        Assert.Null(emptyOver.App);
    }

    [Fact]
    public void App_selector_is_visible_in_auto_and_hidden_in_system_mix()
    {
        var (auto, _, _, _, _, _, _) = MakeConsole(Auto(null));
        Assert.True(auto.ShowAppSelector);

        var (mix, _, _, _, _, _, _) = MakeConsole(SystemMix());
        Assert.False(mix.ShowAppSelector);
    }

    [Fact]
    public void Auto_base_does_not_seed_the_override_until_the_user_picks()
    {
        var (console, _, _, over, _, _, _) = MakeConsole(Auto("Webex"));
        Assert.Null(over.App);                                     // untouched Auto -> auto-detect stands

        console.SessionTargetApp = "Zoom";                        // explicit pick
        Assert.Equal("Zoom", over.App);                           // now forces per-process (Task 7)
    }

    [Fact]
    public void PerProcess_base_still_seeds_the_override()
    {
        var (console, _, _, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", console.SessionTargetApp);
        Assert.Equal("Webex", over.App);
    }

    [Fact]
    public void Selector_edit_mirrors_into_the_override_trimmed()
    {
        var (console, _, _, over, _, _, _) = MakeConsole();

        console.SessionTargetApp = "  Zoom  ";
        Assert.Equal("Zoom", over.App);

        console.SessionTargetApp = "";
        Assert.Null(over.App);

        console.SessionTargetApp = "   ";
        Assert.Null(over.App);
    }

    [Fact]
    public void Selector_edit_never_writes_settings()
    {
        var (console, settings, _, _, _, _, _) = MakeConsole(PerProcess("Webex"));

        console.SessionTargetApp = "Zoom";
        console.SessionTargetApp = "CiscoCollabHost";

        Assert.Equal(0, settings.SaveCount);
        Assert.Equal("Webex", settings.Current.Remote.App);   // saved default untouched
    }

    [Fact]
    public async Task Session_stop_reseeds_the_selector_to_the_saved_default()
    {
        var (console, _, session, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        console.SessionTargetApp = "Zoom";

        await session.StartCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Recording, session.State);
        // Start must NOT clobber an armed override - the user picked "Zoom" for THIS session.
        Assert.Equal("Zoom", console.SessionTargetApp);
        Assert.Equal("Zoom", over.App);

        await session.StopCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal("Webex", console.SessionTargetApp);      // next session = saved default
        Assert.Equal("Webex", over.App);
    }

    [Fact]
    public async Task Settings_change_reseeds_an_untouched_selector()
    {
        var (console, settings, _, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        var raised = new List<string>();
        console.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        await settings.SaveAsync(PerProcess("CiscoCollabHost"), CancellationToken.None);

        Assert.Equal("CiscoCollabHost", console.SessionTargetApp);
        Assert.Equal("CiscoCollabHost", over.App);
        Assert.Contains(nameof(RecordingConsoleViewModel.RemoteSummary), raised);
    }

    [Fact]
    public async Task Settings_change_keeps_a_user_diverged_selector()
    {
        var (console, settings, _, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        console.SessionTargetApp = "Zoom";                    // in-flight per-session edit

        await settings.SaveAsync(PerProcess("CiscoCollabHost"), CancellationToken.None);

        Assert.Equal("Zoom", console.SessionTargetApp);       // never clobbered by a save
        Assert.Equal("Zoom", over.App);
    }

    [Fact]
    public void Summaries_describe_each_mode()
    {
        var (systemMix, _, _, _, _, _, _) = MakeConsole(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix } });
        Assert.Equal("Remote audio: full system mix", systemMix.RemoteSummary);

        var (perApp, _, _, _, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Remote audio: per-app (Webex)", perApp.RemoteSummary);

        var (noApp, _, _, _, _, _, _) = MakeConsole(PerProcess(null));
        Assert.Equal("Remote audio: per-app (no app set - will fall back to system mix)",
            noApp.RemoteSummary);

        var (auto, _, _, _, _, _, _) = MakeConsole(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        Assert.Equal("Remote audio: auto (Webex/Zoom per-app when found, else system mix)",
            auto.RemoteSummary);

        Assert.Equal("Microphone: follows the Windows Communications default", auto.MicSummary);

        // Final-review fix wave: MicSummary now derives from SelectedMic (the applied choice),
        // not the raw Settings.Mic value, so it can never disagree with the dropdown/capture.
        // A pin whose device IS present resolves SelectedMic to that device -> "pinned - Name".
        var (pinned, _, _, _, _, _, _) = MakeConsole(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" } });
        Assert.Equal("Microphone: pinned - Headset Microphone", pinned.MicSummary);
    }

    [Fact]
    public async Task ShowAppSelector_follows_the_settings_mode()
    {
        var (console, settings, _, _, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.True(console.ShowAppSelector);
        var raised = new List<string>();
        console.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        await settings.SaveAsync(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix } }, CancellationToken.None);

        Assert.False(console.ShowAppSelector);
        Assert.Contains(nameof(RecordingConsoleViewModel.ShowAppSelector), raised);
    }

    [Fact]
    public async Task Dispose_unsubscribes_settings_and_session()
    {
        var (console, settings, session, over, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", console.SessionTargetApp);      // untouched selector

        console.Dispose();
        console.Dispose();                                    // idempotent - must not throw
        var raised = new List<string>();
        console.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        // Settings leg: an UNTOUCHED selector would re-seed if still subscribed (see
        // Settings_change_reseeds_an_untouched_selector) - after Dispose it must not.
        await settings.SaveAsync(PerProcess("CiscoCollabHost"), CancellationToken.None);
        Assert.Equal("Webex", console.SessionTargetApp);
        Assert.Equal("Webex", over.App);

        // Session leg: a return to Idle would re-seed from Current ("CiscoCollabHost" now)
        // if still subscribed - after Dispose it must not. State's setter is public
        // (generated by [ObservableProperty]); MetadataEditorViewModelTests'
        // Dispose_unsubscribes test drives it the same way.
        session.State = SessionState.Recording;
        session.State = SessionState.Idle;
        Assert.Equal("Webex", console.SessionTargetApp);

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
        // (the same handler that reverts the app-target selector) clears the picks on Idle.
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

    // --- Final-review fix wave: RemoteSummary/MicSummary must derive from the APPLIED plan
    // (override + selection), never the base settings, so the console can never disagree with
    // what capture will actually do.

    [Fact]
    public void Auto_pick_updates_remote_summary_to_the_chosen_app()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));

        console.SessionTargetApp = "Zoom";

        Assert.Contains("per-app (Zoom)", console.RemoteSummary);
    }

    [Fact]
    public async Task Switching_to_system_mix_clears_a_diverged_app_override()
    {
        var (console, settings, _, over, _, _, _) = MakeConsole(Auto(null));
        console.SessionTargetApp = "Zoom";
        Assert.Equal("Zoom", over.App);                            // armed override

        await settings.SaveAsync(SystemMix(), CancellationToken.None);

        Assert.Null(over.App);
        Assert.False(console.ShowAppSelector);
        Assert.Equal("Remote audio: full system mix", console.RemoteSummary);
    }

    [Fact]
    public void Console_mic_summary_follows_dropdown_for_absent_pin()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-not-present", Name = "Ghost Mic" } });

        Assert.Null(console.SelectedMic.Id);
        Assert.Contains("follows the Windows Communications default", console.MicSummary);
        Assert.DoesNotContain("pinned", console.MicSummary);
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
}

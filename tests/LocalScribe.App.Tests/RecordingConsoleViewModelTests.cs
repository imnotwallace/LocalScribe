using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 Phase 3 C1: the Record console's idle-state VM. It derives the capture
/// summary from Settings (what Start WILL do - no live WASAPI probe, locked decision 3) and
/// owns the per-session target-app selector, which mirrors into RemoteAppOverride (trimmed,
/// empty -> null) and NEVER writes settings.json. Harness: real SessionViewModel over
/// LiveTestDoubles.MakeController (the SessionViewModelTests pattern), synchronous
/// FakeSettingsService, dispatch a => a() so assertions stay synchronous.</summary>
public sealed class RecordingConsoleViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-console-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static Settings PerProcess(string? app) => new()
    { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = app } };

    private (RecordingConsoleViewModel Console, FakeSettingsService Settings,
        SessionViewModel Session, RemoteAppOverride Override) MakeConsole(Settings? initial = null)
    {
        var settings = new FakeSettingsService(initial ?? PerProcess("Webex"));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());      // test VAD, preflight off
        var over = new RemoteAppOverride();
        var console = new RecordingConsoleViewModel(settings, session, over, dispatch: a => a());
        return (console, settings, session, over);
    }

    [Fact]
    public void Seeds_selector_and_override_from_settings_at_construction()
    {
        var (console, _, _, over) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Webex", console.SessionTargetApp);
        Assert.Equal("Webex", over.App);

        var (empty, _, _, emptyOver) = MakeConsole(PerProcess(null));
        Assert.Equal("", empty.SessionTargetApp);
        Assert.Null(emptyOver.App);
    }

    [Fact]
    public void Selector_edit_mirrors_into_the_override_trimmed()
    {
        var (console, _, _, over) = MakeConsole();

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
        var (console, settings, _, _) = MakeConsole(PerProcess("Webex"));

        console.SessionTargetApp = "Zoom";
        console.SessionTargetApp = "CiscoCollabHost";

        Assert.Equal(0, settings.SaveCount);
        Assert.Equal("Webex", settings.Current.Remote.App);   // saved default untouched
    }

    [Fact]
    public async Task Session_stop_reseeds_the_selector_to_the_saved_default()
    {
        var (console, _, session, over) = MakeConsole(PerProcess("Webex"));
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
        var (console, settings, _, over) = MakeConsole(PerProcess("Webex"));
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
        var (console, settings, _, over) = MakeConsole(PerProcess("Webex"));
        console.SessionTargetApp = "Zoom";                    // in-flight per-session edit

        await settings.SaveAsync(PerProcess("CiscoCollabHost"), CancellationToken.None);

        Assert.Equal("Zoom", console.SessionTargetApp);       // never clobbered by a save
        Assert.Equal("Zoom", over.App);
    }

    [Fact]
    public void Summaries_describe_each_mode()
    {
        var (systemMix, _, _, _) = MakeConsole(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix } });
        Assert.Equal("Remote audio: full system mix", systemMix.RemoteSummary);

        var (perApp, _, _, _) = MakeConsole(PerProcess("Webex"));
        Assert.Equal("Remote audio: per-app (Webex)", perApp.RemoteSummary);

        var (noApp, _, _, _) = MakeConsole(PerProcess(null));
        Assert.Equal("Remote audio: per-app (no app set - will fall back to system mix)",
            noApp.RemoteSummary);

        var (auto, _, _, _) = MakeConsole(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        Assert.Equal("Remote audio: auto (Webex/Zoom per-app when found, else system mix)",
            auto.RemoteSummary);

        Assert.Equal("Microphone: follows the Windows Communications default", auto.MicSummary);

        var (pinned, _, _, _) = MakeConsole(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Name = "USB Mic" } });
        Assert.Equal("Microphone: pinned - USB Mic", pinned.MicSummary);
    }

    [Fact]
    public async Task ShowAppSelector_follows_the_settings_mode()
    {
        var (console, settings, _, _) = MakeConsole(PerProcess("Webex"));
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
        var (console, settings, session, over) = MakeConsole(PerProcess("Webex"));
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
}

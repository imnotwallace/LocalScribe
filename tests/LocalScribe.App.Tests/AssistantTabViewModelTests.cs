using System.IO;
using System.Runtime.CompilerServices;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class AssistantTabViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-assist-tab-").FullName;
    private readonly StoragePaths _paths;
    private readonly SummaryStore _store;
    public AssistantTabViewModelTests() { _paths = new StoragePaths(_root); _store = new SummaryStore(_paths); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeRunner(Func<AssistantRequest, IEnumerable<AssistantEvent>> script) : IAssistantJobRunner
    {
        public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in script(request)) { await Task.Yield(); yield return e; }
        }
    }

    private static readonly AssistantModelInfo Model =
        new("Qwen3-4B-Instruct-2507", @"C:\m\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");

    private static LoadedProjection Projection()
    {
        var started = new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
        var rows = new List<DisplayRow>
        { new() { DisplayName = "Sam", Text = "We agreed to file Tuesday.", StartMs = 0, EndMs = 2000 } };
        return new LoadedProjection(
            new SessionRecord(), SessionMeta.CreateDefault(AppKind.Webex, started, self: null),
            [], null, null, new Dictionary<string, Matter>(), [], started, rows,
            new TranscriptHeader("t", "Webex", started, 0, "base.en", "CPU"),
            new SessionTextView("t", [], [], started, null, 0, "call", "", null), "v1");
    }

    private AssistantTabViewModel MakeVm(
        Func<AssistantRequest, IEnumerable<AssistantEvent>>? script = null,
        bool enabled = true, bool anyModel = true, bool helper = true)
    {
        var runner = new FakeRunner(script ?? (_ =>
            [new AssistantChunk("## Summary\nFiled Tuesday."), new AssistantDone("cpu", 5, 3)]));
        var cache = new AssistantManifestCache(_ => Task.FromResult(new AssistantModelManifest(
            anyModel ? [Model] : [], anyModel ? Model : null, [])));
        var settings = new FakeSettingsService(new Settings
        { Assistant = new AssistantSetting { Enabled = enabled } });
        var summarizer = new SummarizationService(_paths, () => settings.Current, TimeProvider.System,
            runner, _store, new AssistantGate(() => null, pollMs: 10), cache,
            loadProjection: (_, _) => Task.FromResult(Projection()));
        return new AssistantTabViewModel(summarizer, _store, cache, settings,
            new FakeUiErrorReporter(), dispatch: a => a(),
            helperProbe: () => helper ? @"C:\app\assistant\LocalScribe.Assistant.exe" : null);
    }

    [Fact]
    public async Task Availability_needs_model_AND_helper_and_explainers_do_not_hide_each_other()
    {
        // Design 2026-07-23 section 4: a missing helper used to be indistinguishable from a
        // working one until the first request failed - exactly how the broken exe shipped.
        var noHelper = MakeVm(helper: false);
        await noHelper.LoadAsync("s1", CancellationToken.None);
        Assert.False(noHelper.AssistantAvailable);
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", noHelper.DisabledExplainer);
        Assert.DoesNotContain("No assistant model", noHelper.DisabledExplainer);

        var neither = MakeVm(anyModel: false, helper: false);
        await neither.LoadAsync("s1", CancellationToken.None);
        Assert.False(neither.AssistantAvailable);
        Assert.Contains("No assistant model", neither.DisabledExplainer);       // both shown -
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", neither.DisabledExplainer); // one fix
                                                                                 // must not hide the other
        var both = MakeVm();
        await both.LoadAsync("s1", CancellationToken.None);
        Assert.True(both.AssistantAvailable);
        Assert.Equal("", both.DisabledExplainer);
    }

    [Fact]
    public async Task Disabled_with_explainer_when_toggle_off_or_no_model()
    {
        // Design 7.6: all assistant UI disabled-with-explainer until a model exists.
        var off = MakeVm(enabled: false);
        await off.LoadAsync("s1", CancellationToken.None);
        Assert.False(off.AssistantAvailable);
        Assert.Contains("turned off in Settings", off.DisabledExplainer);
        Assert.False(off.RegenerateCommand.CanExecute(null));

        var noModel = MakeVm(anyModel: false);
        await noModel.LoadAsync("s1", CancellationToken.None);
        Assert.False(noModel.AssistantAvailable);
        Assert.Contains("No assistant model", noModel.DisabledExplainer);
    }

    [Fact]
    public async Task Regenerate_streams_persists_and_selects_the_new_version_with_the_label()
    {
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);
        Assert.True(vm.AssistantAvailable);
        Assert.Empty(vm.Versions);

        await vm.RegenerateCommand.ExecuteAsync(null);

        var v = Assert.Single(vm.Versions);
        Assert.Same(v, vm.SelectedVersion);
        Assert.Equal("## Summary\nFiled Tuesday.", vm.ContentText);
        Assert.False(vm.IsStale);
        Assert.Equal("", vm.ErrorText);
        Assert.Contains("q4b.gguf", vm.VersionInfo);                 // provenance line
        Assert.Contains("CPU", vm.VersionInfo);                      // ACTUAL backend surfaced
        Assert.Equal(AssistantPrompts.DraftLabel, vm.DraftLabel);    // the locked label
        Assert.Single(await _store.LoadAsync("s1", CancellationToken.None));   // really persisted
    }

    [Fact]
    public async Task Fall_is_stated_on_the_provenance_line_and_live_phase_is_friendly()
    {
        // Design 2026-07-23 section 7: the fall is stated in words on the version's provenance
        // line (not just the raw wire phase scrolling past in the live status).
        var vm = MakeVm(script: _ =>
            [new AssistantProgress(AssistantWire.CudaFellPhase, 0, 0),
             new AssistantChunk("## S"), new AssistantDone("cpu", 5, 3)]);
        await vm.LoadAsync("s1", CancellationToken.None);
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Contains("CPU", vm.VersionInfo);
        Assert.Contains("fell to CPU", vm.VersionInfo);      // stated explicitly (design section 7)

        var noFall = MakeVm();   // default script reports plain cpu, no fall event
        await noFall.LoadAsync("s1", CancellationToken.None);
        await noFall.RegenerateCommand.ExecuteAsync(null);
        Assert.DoesNotContain("fell to CPU", noFall.VersionInfo);
    }

    [Fact]
    public async Task Live_phase_text_translates_the_wire_fall_phase_into_plain_words()
    {
        // The raw wire phase ("cuda-fell-to-cpu") must never be the live status the user reads.
        // Hold the job open just after the fall event so the transient PhaseText is observable
        // (RegenerateAsync clears it in its finally).
        using var release = new ManualResetEventSlim(false);
        IEnumerable<AssistantEvent> Script(AssistantRequest _)
        {
            yield return new AssistantProgress(AssistantWire.CudaFellPhase, 0, 0);
            release.Wait(TimeSpan.FromSeconds(5));
            yield return new AssistantChunk("## S");
            yield return new AssistantDone("cpu", 5, 3);
        }

        var vm = MakeVm(script: Script);
        await vm.LoadAsync("s1", CancellationToken.None);
        var running = vm.RegenerateCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => vm.PhaseText.Length > 0, TimeSpan.FromSeconds(5)),
            "the fall never reached the live phase line");
        Assert.Equal("GPU unavailable - continuing on CPU", vm.PhaseText);

        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Error_is_visible_and_persists_nothing()
    {
        // Design 7.7: helper crash -> visible error, nothing persisted.
        var vm = MakeVm(_ => [new AssistantError("JOB_FAILED: boom")]);
        await vm.LoadAsync("s1", CancellationToken.None);
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Contains("boom", vm.ErrorText);
        Assert.Empty(vm.Versions);
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Regenerate_queues_visibly_while_recording_and_persists_nothing_until_idle()
    {
        // Design 7.1/7.7: mid-recording -> visibly queued (never refused, never auto-run), and
        // NOTHING persists while blocked. Mirrors SummarizationServiceTests' Queued_while_recording
        // pattern: a mutable busy probe that starts blocking and is flipped to idle to unwind
        // cleanly (AssistantGate.EnterAsync polls recordingBusy() every pollMs and only exits its
        // wait loop - honoring cancellation or a cleared reason - it never times out on its own).
        string? busy = "recording";
        int runCount = 0;
        var runner = new FakeRunner(_ =>
        {
            runCount++;
            return [new AssistantChunk("## Summary\nFiled Tuesday."), new AssistantDone("cpu", 5, 3)];
        });
        var cache = new AssistantManifestCache(_ => Task.FromResult(new AssistantModelManifest([Model], Model, [])));
        var settings = new FakeSettingsService(new Settings { Assistant = new AssistantSetting { Enabled = true } });
        var gate = new AssistantGate(() => busy, pollMs: 1);
        var summarizer = new SummarizationService(_paths, () => settings.Current, TimeProvider.System,
            runner, _store, gate, cache, loadProjection: (_, _) => Task.FromResult(Projection()));
        var vm = new AssistantTabViewModel(summarizer, _store, cache, settings,
            new FakeUiErrorReporter(), dispatch: a => a(),
            helperProbe: () => @"C:\app\assistant\LocalScribe.Assistant.exe");
        await vm.LoadAsync("s1", CancellationToken.None);

        var running = vm.RegenerateCommand.ExecuteAsync(null);

        Assert.True(SpinWait.SpinUntil(() => vm.WaitingText.Length > 0, TimeSpan.FromSeconds(5)),
            "queued job never surfaced a waiting message");
        Assert.Equal("recording", vm.WaitingText);           // the visible waiting message
        Assert.Equal(0, runCount);                            // did NOT run while "recording"
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));   // nothing persisted

        busy = null;                                          // recording clears -> unwind
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsRunning);
        Assert.Equal(1, runCount);                            // ran exactly once idle
        Assert.Single(await _store.LoadAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task Stale_badge_follows_the_selected_stored_version()
    {
        await _store.AppendAsync("s1", new SummaryVersion("s1", DateTimeOffset.UtcNow, "v1",
            new AssistantModelRef("q4b.gguf", new string('a', 64), "cuda"),
            AssistantPrompts.PromptVersion, "## Summary\nOld.", Stale: true), CancellationToken.None);
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);
        Assert.True(vm.IsStale);                                     // the stale badge state
        Assert.Equal("## Summary\nOld.", vm.ContentText);            // old versions stay readable
    }

    [Fact]
    public async Task Store_marked_stale_shows_the_badge_on_reload()
    {
        // The wiring calls MarkAllStaleAsync on SessionContentChanged; this pins the
        // store->tab half of that path (the event->store half is the one-line delegate above,
        // exercised by the existing MaintenanceService event coverage).
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.False(vm.IsStale);

        await _store.MarkAllStaleAsync("s1", CancellationToken.None);   // = the wired reaction
        await vm.LoadAsync("s1", CancellationToken.None);
        Assert.True(vm.IsStale);
    }
}

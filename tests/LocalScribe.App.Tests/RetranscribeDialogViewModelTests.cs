using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class RetranscribeDialogViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-retrans-dialog-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public RetranscribeDialogViewModelTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteBurstWav(string path)
    {
        using var sink = new WavSink(path);
        sink.Write(new float[16 * 300]);
        var burst = new float[16 * 1500];
        for (int i = 0; i < burst.Length; i++)
            burst[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / 16000.0));
        sink.Write(burst);
        sink.Write(new float[16 * 1000]);
    }

    private async Task<string> SeedFinalizedAsync(string id = "2026-07-10_1000_Webex_seed")
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero),
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
            Sources = [SourceKind.Local], RetainedAudioSources = [SourceKind.Local],
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        WriteBurstWav(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Wav));
        return id;
    }

    private (RetranscribeDialogViewModel Vm, RetranscriptionRunner Runner, MaintenanceService Maint,
        FakeUiErrorReporter Errors) Make(string sessionId, IReadOnlySet<string>? models = null)
    {
        var settings = new Settings();
        var maint = new MaintenanceService(_paths, new FakeSettingsService(settings),
            new FakeRecycleBin(), TimeProvider.System);
        var modelSet = models ?? new HashSet<string> { "base.en", "tiny.en" };
        var runner = new RetranscriptionRunner(_paths, () => settings, new FakeEngineFactory(),
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero)),
            liveEngineBusy: () => null, availableModels: () => modelSet);
        var errors = new FakeUiErrorReporter();
        var vm = new RetranscribeDialogViewModel(sessionId, maint, runner, () => modelSet,
            errors, dispatch: a => a());
        return (vm, runner, maint, errors);
    }

    [Fact]
    public async Task ModelChoices_list_only_disk_models_and_gate_Start()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id);
        Assert.Equal(new[] { "base.en", "tiny.en" }, vm.ModelChoices);   // Ordinal-sorted, no "auto"
        Assert.Equal("base.en", vm.SelectedModel);
        Assert.Equal("auto", vm.Language);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.CancelRunCommand.CanExecute(null));
        vm.Dispose();

        var (empty, _, _, _) = Make(id, models: new HashSet<string>());
        Assert.Empty(empty.ModelChoices);
        Assert.Null(empty.SelectedModel);
        Assert.False(empty.StartCommand.CanExecute(null));               // nothing on disk -> no Start
        empty.Dispose();
    }

    [Fact]
    public async Task LoadAsync_shows_the_current_version_line()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, _) = Make(id);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Contains("v1", vm.CurrentVersionDisplay);
        Assert.Contains("small.en", vm.CurrentVersionDisplay);
        vm.Dispose();
    }

    [Fact]
    public async Task Start_runs_to_a_committed_version_infos_and_closes()
    {
        string id = await SeedFinalizedAsync();
        var (vm, _, _, errors) = Make(id);
        vm.Language = "en";
        bool closed = false;
        vm.Closed += () => closed = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.False(vm.IsRunning);
        Assert.Contains(errors.Infos, m => m.Contains("v2"));
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.StartsWith("v2-base.en-", session!.ActiveVersion);
        vm.Dispose();
    }

    [Fact]
    public async Task Dialog_for_a_different_session_does_not_reflect_or_enable_cancelling_anothers_run()
    {
        // F3 (whole-branch review): IsRunning/CancelRunCommand must be gated on THIS dialog's own
        // session, not the runner's GLOBAL RunningSessionId - otherwise a dialog opened for
        // session B would show IsRunning=true and enable Cancel while session A's unrelated run is
        // in flight (RetranscriptionRunner.CancelCurrent has no session scoping of its own - it
        // always cancels whatever is currently running, so an enabled Cancel on B's dialog would
        // actually cancel A's run).
        string idA = await SeedFinalizedAsync("2026-07-10_1000_Webex_a");
        string idB = await SeedFinalizedAsync("2026-07-10_1100_Webex_b");
        var settings = new Settings();
        var maint = new MaintenanceService(_paths, new FakeSettingsService(settings),
            new FakeRecycleBin(), TimeProvider.System);
        var modelSet = new HashSet<string> { "base.en", "tiny.en" };
        var gated = new GatedEngineFactory();
        // ONE shared runner behind both dialogs - exactly how CompositionRoot wires a single
        // app-lifetime RetranscriptionRunner behind every Re-transcribe dialog instance.
        var runner = new RetranscriptionRunner(_paths, () => settings, gated,
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 13, 6, 0, 0, TimeSpan.Zero)),
            liveEngineBusy: () => null, availableModels: () => modelSet);
        var vmA = new RetranscribeDialogViewModel(idA, maint, runner, () => modelSet,
            new FakeUiErrorReporter(), dispatch: a => a());
        var vmB = new RetranscribeDialogViewModel(idB, maint, runner, () => modelSet,
            new FakeUiErrorReporter(), dispatch: a => a());

        // Start A's run and let it block inside the gated engine creation - RunningSessionId == idA.
        var runTask = runner.RunAsync(new RetranscriptionRequest
        { SessionId = idA, Model = "base.en", Language = "en" }, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => vmA.IsRunning, TimeSpan.FromSeconds(10)));

        // A's OWN dialog correctly reflects and can cancel its own run...
        Assert.True(vmA.IsRunning);
        Assert.True(vmA.CancelRunCommand.CanExecute(null));
        // ...but B's dialog (a DIFFERENT session) must not - this is the defect this fix closes.
        Assert.False(vmB.IsRunning);
        Assert.False(vmB.CancelRunCommand.CanExecute(null));

        gated.CreateGate.Set();                       // release the parked engine build
        string? vid = await runTask;

        Assert.NotNull(vid);                          // A's run completed normally - never cancelled
        vmA.Dispose();
        vmB.Dispose();
    }
}

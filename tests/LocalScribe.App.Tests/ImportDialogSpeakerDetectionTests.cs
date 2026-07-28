using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Task 8 (design 2026-07-28 section 3): the Import dialog's Speakers control, the
/// availability gate and the detect-stage progress. Separate file from ImportDialogViewModelTests
/// per house convention - its own copy of the harness (FakeDecoder, QueuedDispatch, etc).</summary>
public sealed class ImportDialogSpeakerDetectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-importdlg-speakers-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public ImportDialogSpeakerDetectionTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeSettings : ISettingsService
    {
        public Settings Current { get; private set; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }
    private sealed class NoopBin : IRecycleBin { public void SendToRecycleBin(string path) { } }
    private sealed class RecordingErrors : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public List<string> Infos { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) => Infos.Add(message);
    }
    private sealed class FakeDecoder : IAudioDecoder
    {
        public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct)
            => throw new NotSupportedException("this file never picks a file - it sets SourcePath directly");
        public Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
            => throw new NotSupportedException("dialog VM never decodes");
    }

    /// <summary>Canonical QUEUED dispatch fake (copied from SplitSpeakersViewModelVoiceprintTests.cs
    /// per house convention): queues actions instead of running them inline, so a test can pump one
    /// turn at a time and assert no dispatch turn ever exposes a half-updated state.</summary>
    private sealed class QueuedDispatch
    {
        private readonly Queue<Action> _queue = new();
        public Action<Action> Dispatch => a => _queue.Enqueue(a);
        public bool PumpOne()
        {
            if (_queue.Count == 0) return false;
            _queue.Dequeue()();
            return true;
        }
        public void Pump() { while (PumpOne()) { } }
    }

    private ImportDialogViewModel MakeVm(Func<string?>? unavailable = null,
        ImportRunner? run = null, Action<Action>? dispatch = null)
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(), new NoopBin(),
            TimeProvider.System);
        var errors = new RecordingErrors();
        return new ImportDialogViewModel(new FakeDecoder(),
            run ?? ((req, progress, tp, dp, confirm, ct) => Task.FromResult("s1")),
            maintenance,
            availableModels: () => new HashSet<string> { "large-v3-turbo" },
            pickOpenPath: _ => null, confirmMismatch: _ => Task.FromResult(true),
            errors, dispatch: dispatch ?? (a => a()), TimeProvider.System,
            speakerDetectionUnavailable: unavailable);
    }

    /// <summary>Sets SourcePath/Title/RecordedAtText directly so CanStart() passes without a real
    /// file pick - this suite exercises the Speakers control, not the probe path.</summary>
    private static Task PickAndFillAsync(ImportDialogViewModel vm)
    {
        vm.SourcePath = @"C:\evidence\call.mp3";
        vm.Title = "Call";
        vm.RecordedAtText = "2026-03-05 14:30";
        return Task.CompletedTask;
    }

    [Fact]
    public void Defaults_to_detect_automatically()
    {
        var vm = MakeVm();
        Assert.Equal(SpeakerDetection.Auto, vm.SelectedSpeakerChoice!.Mode);
        Assert.Null(vm.SelectedSpeakerChoice.Count);
    }

    [Fact]
    public void Offers_off_auto_and_counts_two_through_six()
    {
        var vm = MakeVm();
        Assert.Equal(SpeakerDetection.Off, vm.SpeakerChoices[0].Mode);
        Assert.Equal(SpeakerDetection.Auto, vm.SpeakerChoices[1].Mode);
        var counts = vm.SpeakerChoices.Where(c => c.Mode == SpeakerDetection.Declared)
            .Select(c => c.Count).ToList();
        Assert.Equal([2, 3, 4, 5, 6], counts);
        // The dropdown never offers a count below 2 - ImportRequest would throw, and
        // SherpaDiarisationRunner.cs:23 would silently take the auto path for a 0.
        Assert.DoesNotContain(vm.SpeakerChoices, c => c.Count is int n && n < 2);
    }

    [Fact]
    public void The_control_is_suppressed_for_a_declared_channel_split()
    {
        var vm = MakeVm();
        vm.IsStereo = true;
        vm.EachPartyOwnChannel = true;
        // Split-stereo already has speakers by channel; detection is not offered.
        Assert.False(vm.CanChooseSpeakers);
    }

    [Fact]
    public void A_stereo_file_the_user_did_not_split_still_offers_detection()
    {
        // Downmix is the DEFAULT answer, and is exactly the case that needs detection.
        var vm = MakeVm();
        vm.IsStereo = true;
        vm.EachPartyOwnChannel = false;
        Assert.True(vm.CanChooseSpeakers);
    }

    [Fact]
    public void An_unavailable_helper_disables_the_control_with_a_visible_reason()
    {
        var vm = MakeVm(unavailable: () => "Speaker detection unavailable - LocalScribe.Diarizer.exe is not installed.");
        Assert.False(vm.CanChooseSpeakers);
        Assert.Contains("LocalScribe.Diarizer.exe", vm.SpeakerDetectionUnavailableReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_writes_the_chosen_mode_and_count_onto_the_request()
    {
        ImportRequest? captured = null;
        var vm = MakeVm(run: (req, _, _, _, _, _) => { captured = req; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        vm.SelectedSpeakerChoice = vm.SpeakerChoices.First(c => c.Count == 3);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(SpeakerDetection.Declared, captured!.SpeakerDetection);
        Assert.Equal(3, captured.SpeakerCount);
    }

    [Fact]
    public async Task Start_sends_Off_when_the_control_is_suppressed_by_a_channel_split()
    {
        ImportRequest? captured = null;
        var vm = MakeVm(run: (req, _, _, _, _, _) => { captured = req; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        vm.IsStereo = true;
        vm.EachPartyOwnChannel = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(SpeakerDetection.Off, captured!.SpeakerDetection);
        Assert.Null(captured.SpeakerCount);
    }

    [Fact]
    public async Task Start_sends_Off_when_the_helper_is_unavailable()
    {
        ImportRequest? captured = null;
        var vm = MakeVm(unavailable: () => "no helper",
            run: (req, _, _, _, _, _) => { captured = req; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(SpeakerDetection.Off, captured!.SpeakerDetection);
    }

    [Fact]
    public async Task The_detect_stage_gets_its_own_text_not_the_saving_catch_all()
    {
        // ImportDialogViewModel.cs's DispatchProgress stage switch has a `_ =>` catch-all printing
        // "Saving session..." and NO explicit Save arm, so a new ImportStage member renders as
        // "Saving session..." with no compiler warning. This test is the only thing that catches that.
        var dispatcher = new QueuedDispatch();
        IProgress<ImportStage>? stages = null;
        var vm = MakeVm(dispatch: dispatcher.Dispatch,
            run: (_, p, _, _, _, _) => { stages = p; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        await vm.StartCommand.ExecuteAsync(null);

        stages!.Report(ImportStage.DetectSpeakers);
        dispatcher.Pump();

        Assert.Contains("speaker", vm.StageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Saving", vm.StageText, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsDetectingSpeakers);
        Assert.False(vm.IsTranscribing);
    }

    [Fact]
    public async Task Detect_progress_drives_a_determinate_bar_and_flips_to_matching_at_the_end()
    {
        // The helper's embedding-extraction tail emits NO progress (Diarizer/Program.cs:61-72), so a
        // bar parked at 100% reads as a hang. At 1.0 the text says what is still happening.
        var dispatcher = new QueuedDispatch();
        IProgress<double>? detect = null;
        var vm = MakeVm(dispatch: dispatcher.Dispatch,
            run: (_, _, _, d, _, _) => { detect = d; return Task.FromResult("s1"); });
        await PickAndFillAsync(vm);
        await vm.StartCommand.ExecuteAsync(null);

        detect!.Report(0.4);
        dispatcher.Pump();
        Assert.Equal(0.4, vm.DetectProgress, 3);
        Assert.Contains("40", vm.DetectProgressText, StringComparison.Ordinal);

        detect.Report(1.0);
        dispatcher.Pump();
        Assert.Contains("Matching voices", vm.DetectProgressText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detect_state_is_cleared_when_the_import_settles()
    {
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(dispatch: dispatcher.Dispatch);
        await PickAndFillAsync(vm);

        await vm.StartCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.False(vm.IsDetectingSpeakers);
        Assert.False(vm.IsBusy);
    }
}

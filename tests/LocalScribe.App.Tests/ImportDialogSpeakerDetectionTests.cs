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
        // Lock-guarded: this stands in for WPF's Dispatcher, whose BeginInvoke is thread-safe from
        // any thread. A fire-and-forget load's pool-thread continuation can enqueue here while the
        // test thread is inside Pump/PumpOne; a plain Queue<Action> corrupts under that concurrent
        // access. Dequeue under the lock, invoke outside it so a re-entrant dispatch cannot deadlock.
        private readonly object _gate = new();
        private readonly Queue<Action> _queue = new();
        public Action<Action> Dispatch => a => { lock (_gate) _queue.Enqueue(a); };
        public bool PumpOne()
        {
            Action next;
            lock (_gate)
            {
                if (_queue.Count == 0) return false;
                next = _queue.Dequeue();
            }
            next();
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
    public async Task Detect_state_is_set_mid_flight_and_cleared_once_settled_including_a_second_import()
    {
        // Fix round 1: removed the old Detect_state_is_cleared_when_the_import_settles test that
        // used to live here. It used the default (immediate-return) runner, which never reports
        // ImportStage.DetectSpeakers - so IsDetectingSpeakers was never true in the first place and
        // the "cleared" assertions passed trivially, both before AND after deleting the actual
        // `finally` reset in StartAsync (verified by mutation - see the fix report). It is fully
        // superseded by this test's Act-1 settle assertions below, which additionally prove the
        // state was genuinely dirtied first.
        //
        // None of the other tests in this class prove the detect state is ever CLEARED either, for
        // the same reason: every other fake runner returns immediately - by the time a test reports
        // a stage or a progress value, StartAsync's `finally` has already run and the fields were
        // never observed dirty in the first place. This test PARKS the runner on a
        // TaskCompletionSource (the idiom ImportDialogViewModelTests.Cancel_during_import_... and
        // Transcription_progress_drives_bar_eta_and_preview already use) so the import stays
        // in flight while the mid-flight assertions run, then releases it normally (not via
        // cancellation) so the settle path - not the cancel path - is what gets pinned.
        var dispatcher = new QueuedDispatch();
        TaskCompletionSource started = new();
        TaskCompletionSource<string> release = new();
        IProgress<ImportStage>? stages = null;
        IProgress<double>? detect = null;
        var vm = MakeVm(dispatch: dispatcher.Dispatch,
            run: async (_, p, _, d, _, ct) =>
            {
                stages = p;
                detect = d;
                started.SetResult();
                return await release.Task;
            });
        await PickAndFillAsync(vm);

        // --- Act 1: mid-flight, DetectSpeakers + a partial report genuinely set the state ---
        var run1 = vm.StartCommand.ExecuteAsync(null);
        await started.Task;

        stages!.Report(ImportStage.DetectSpeakers);
        dispatcher.Pump();
        detect!.Report(0.4);
        dispatcher.Pump();

        Assert.True(vm.IsDetectingSpeakers);
        Assert.Equal(0.4, vm.DetectProgress, 3);
        Assert.False(string.IsNullOrEmpty(vm.DetectProgressText));

        // --- Release normally (settle), not by cancelling - the `finally` reset is what this pins ---
        release.SetResult("s1");
        await run1;
        dispatcher.Pump();

        Assert.False(vm.IsDetectingSpeakers);
        Assert.Equal(0, vm.DetectProgress);
        Assert.Equal("", vm.DetectProgressText);
        Assert.False(vm.IsBusy);

        // --- Act 2: pins the reset at the TOP of StartAsync, distinct from the one in `finally`.
        // Simulate a stray/late progress report landing on the OLD channel after the first import
        // already settled (e.g. a slow-flushing background reporter) - `finally` cannot have
        // guarded against this, since it already ran. A second import on the SAME dialog instance
        // must still begin from clean state, before any of its OWN stage reports land.
        stages.Report(ImportStage.DetectSpeakers);
        detect.Report(0.9);
        dispatcher.Pump();
        Assert.True(vm.IsDetectingSpeakers);          // sanity: the stray report really dirtied it
        Assert.Equal(0.9, vm.DetectProgress, 3);

        started = new TaskCompletionSource();
        release = new TaskCompletionSource<string>();
        var run2 = vm.StartCommand.ExecuteAsync(null);
        await started.Task;

        Assert.False(vm.IsDetectingSpeakers);
        Assert.Equal(0, vm.DetectProgress);
        Assert.Equal("", vm.DetectProgressText);

        release.SetResult("s2");
        await run2;
    }
}

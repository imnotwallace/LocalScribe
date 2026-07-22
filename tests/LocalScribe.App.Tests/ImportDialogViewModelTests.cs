using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Import;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ImportDialogViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-importdlg-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public ImportDialogViewModelTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeSettings2 : ISettingsService
    {
        public Settings Current { get; private set; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }
    private sealed class NoopBin2 : IRecycleBin { public void SendToRecycleBin(string path) { } }
    private sealed class RecordingErrors2 : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public List<string> Infos { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) => Infos.Add(message);
    }
    private sealed class FakeDecoder : IAudioDecoder
    {
        public AudioProbeResult Probe { get; set; } = new();
        public Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct) => Task.FromResult(Probe);
        public Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
            => throw new NotSupportedException("dialog VM never decodes");
    }
    /// <summary>Fixed +10:00 zone so date-default and parse asserts are machine-independent.</summary>
    private sealed class FixedZoneTime : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
            "dlg-test-zone", TimeSpan.FromHours(10), "dlg-test-zone", "dlg-test-zone");
    }

    /// <summary>A clock the test can advance by hand, so ETA math (elapsed x (1-f)/f) is
    /// deterministic instead of racing the real wall clock.</summary>
    private sealed class AdvanceableTime : TimeProvider
    {
        private DateTimeOffset _now;
        public AdvanceableTime(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    private (ImportDialogViewModel Vm, FakeDecoder Decoder, RecordingErrors2 Errors)
        MakeVm(ImportRunner? runner = null, string? pickedPath = null, TimeProvider? time = null)
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings2(), new NoopBin2(),
            TimeProvider.System);
        var decoder = new FakeDecoder();
        var errors = new RecordingErrors2();
        var vm = new ImportDialogViewModel(decoder,
            runner ?? ((req, progress, tp, confirm, ct) => Task.FromResult("session-1")),
            maintenance, pickOpenPath: _ => pickedPath, confirmMismatch: _ => Task.FromResult(true),
            errors, dispatch: a => a(), time ?? new FixedZoneTime());
        return (vm, decoder, errors);
    }

    [Fact]
    public async Task PickFile_probes_and_defaults_title_and_recorded_date_from_media_tag()
    {
        var (vm, decoder, _) = MakeVm(pickedPath: @"C:\evidence\hearing recording.m4a");
        decoder.Probe = new AudioProbeResult
        {
            FormatName = "mov,mp4,m4a,3gp,3g2,mj2", FileSizeBytes = 3_500_000,
            ClaimedDurationMs = 754_000, ClaimedChannels = 1, ClaimedSampleRate = 44100,
            MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
        };
        Assert.False(vm.StartCommand.CanExecute(null));      // no file yet

        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.True(vm.HasFile);
        Assert.Equal("hearing recording.m4a", vm.FileNameDisplay);
        Assert.Equal("hearing recording", vm.Title);         // filename stem
        Assert.Equal("12:34", vm.DurationDisplay);
        Assert.Equal("3.3 MB", vm.SizeDisplay);
        Assert.Equal("MOV", vm.FormatDisplay);               // first format_name token
        Assert.Equal("2026-03-05 14:30", vm.RecordedAtText); // media tag -> +10:00 wall time
        Assert.False(vm.IsStereo);                           // 1 channel: no stereo question
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecordedAt_falls_back_to_earliest_file_timestamp_and_validates()
    {
        var (vm, decoder, _) = MakeVm(pickedPath: @"C:\evidence\call.mp3");
        decoder.Probe = new AudioProbeResult
        {
            FormatName = "mp3", ClaimedChannels = 2,
            FileCreatedUtc = new DateTimeOffset(2026, 3, 6, 2, 0, 0, TimeSpan.Zero),
            FileModifiedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),   // earlier
        };
        await vm.PickFileCommand.ExecuteAsync(null);

        Assert.Equal("2026-03-05 14:30", vm.RecordedAtText); // earliest timestamp, +10:00 wall time
        Assert.True(vm.IsStereo);                            // 2 claimed channels: ask the question
        Assert.Null(vm.RecordedAtError);

        vm.RecordedAtText = "not a date";
        Assert.NotNull(vm.RecordedAtError);
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.RecordedAtText = "2026-03-05 15:00";
        Assert.Null(vm.RecordedAtError);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_builds_the_request_reports_stages_and_completes()
    {
        ImportRequest? captured = null;
        ImportRunner runner = (req, progress, tp, confirm, ct) =>
        {
            captured = req;
            progress.Report(ImportStage.Copy);
            progress.Report(ImportStage.Decode);
            progress.Report(ImportStage.Transcribe);
            progress.Report(ImportStage.Save);
            return Task.FromResult("2026-03-05_1430_Manual_hearing");
        };
        var (vm, decoder, errors) = MakeVm(runner, pickedPath: @"C:\evidence\hearing.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3", ClaimedChannels = 2 };
        await vm.PickFileCommand.ExecuteAsync(null);
        await vm.LoadMattersAsync();                          // empty catalog: no matters, no crash

        vm.Title = "  Hearing day 1  ";
        vm.RecordedAtText = "2026-03-05 14:30";
        vm.EachPartyOwnChannel = true;
        vm.SwapSides = true;
        var stages = new List<string>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StageText) && vm.StageText.Length > 0) stages.Add(vm.StageText); };
        string? completedId = null;
        bool closed = false;
        vm.Completed += id => completedId = id;
        vm.CloseRequested += () => closed = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\evidence\hearing.mp3", captured!.SourcePath);
        Assert.Equal("Hearing day 1", captured.Title);        // trimmed
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
            captured.RecordedAtLocal);                        // FixedZoneTime offset applied
        Assert.Equal(StereoMapping.SplitSwapped, captured.Stereo);
        Assert.Empty(captured.MatterIds);
        Assert.Equal(4, stages.Count);                        // one text per stage
        Assert.Equal("2026-03-05_1430_Manual_hearing", completedId);
        Assert.True(closed);
        Assert.False(vm.IsBusy);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task LoadMatters_surfaces_a_broken_catalog_read_via_Info()
    {
        // A matters.json written by a NEWER app version makes ListMattersAsync throw
        // (SchemaGuard.RejectIfNewer). In a never-silent app that must reach the user, not just
        // Debug.WriteLine - the picker is optional, but a broken catalog read should say so.
        Directory.CreateDirectory(_paths.MattersDir);
        await File.WriteAllTextAsync(_paths.MattersIndexJson, "{\"schemaVersion\":999}");
        var (vm, _, errors) = MakeVm();

        await vm.LoadMattersAsync();

        Assert.Empty(vm.MatterOptions);                       // failed read -> empty picker (unchanged)
        Assert.Empty(errors.Reports);                         // best-effort load: Info, not Report
        Assert.Contains(errors.Infos, m => m.Contains("matter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Matter_toggle_and_filter_drive_the_picker_options()
    {
        // B3-3 sub-item: the matter picker (toggle + query filter) had no test. Pin that a toggle
        // selects and the query narrows the options while a prior selection survives the round-trip.
        var store = new MatterStore(_paths.MattersDir);
        await store.SaveAsync(new Matter { Id = "M-1", Name = "Acme v Widget", Reference = "AC-1" },
            CancellationToken.None);
        await store.SaveAsync(new Matter { Id = "M-2", Name = "Beta Corp", Reference = "BC-2" },
            CancellationToken.None);
        var (vm, _, _) = MakeVm();

        await vm.LoadMattersAsync();
        Assert.Equal(2, vm.MatterOptions.Count);

        vm.ToggleMatterCommand.Execute(vm.MatterOptions.Single(o => o.Id == "M-1"));
        Assert.True(vm.MatterOptions.Single(o => o.Id == "M-1").IsSelected);

        vm.MatterPickerQuery = "beta";                        // filter by name
        Assert.Equal("M-2", Assert.Single(vm.MatterOptions).Id);

        vm.MatterPickerQuery = "";
        Assert.True(vm.MatterOptions.Single(o => o.Id == "M-1").IsSelected);   // selection survived
    }

    [Fact]
    public async Task Stereo_answers_map_to_the_three_mappings()
    {
        var mappings = new List<StereoMapping>();
        ImportRunner runner = (req, p, tp, c, ct) => { mappings.Add(req.Stereo); return Task.FromResult("s"); };
        var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3", ClaimedChannels = 2 };
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.RecordedAtText = "2026-03-05 14:30";

        vm.EachPartyOwnChannel = false;                       // "No/unsure"
        await vm.StartCommand.ExecuteAsync(null);
        vm.EachPartyOwnChannel = true; vm.SwapSides = false;   // Yes, L = me
        await vm.StartCommand.ExecuteAsync(null);
        vm.EachPartyOwnChannel = true; vm.SwapSides = true;    // Yes, swapped
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal([StereoMapping.Downmix, StereoMapping.Split, StereoMapping.SplitSwapped], mappings);
    }

    [Fact]
    public async Task Cancel_during_import_cancels_the_token_and_reports_info_not_error()
    {
        var started = new TaskCompletionSource();
        ImportRunner runner = async (req, p, tp, c, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);           // parks until cancelled
            return "never";
        };
        var (vm, decoder, errors) = MakeVm(runner, pickedPath: @"C:\a.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.RecordedAtText = "2026-03-05 14:30";
        bool completed = false;
        vm.Completed += _ => completed = true;

        var run = vm.StartCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsBusy);
        vm.CancelCommand.Execute(null);                       // busy: cancels, does NOT close
        await run;

        Assert.False(vm.IsBusy);
        Assert.False(completed);
        Assert.Empty(errors.Reports);                         // cancellation is not an error
        Assert.Contains(errors.Infos, m => m.Contains("cancelled"));

        bool closed = false;
        vm.CloseRequested += () => closed = true;
        vm.CancelCommand.Execute(null);                       // idle: requests close
        Assert.True(closed);
    }

    [Fact]
    public async Task Transcription_progress_drives_bar_eta_and_preview()
    {
        var clock = new AdvanceableTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        IProgress<TranscriptionProgress>? tp = null;
        var started = new TaskCompletionSource();
        ImportRunner runner = async (req, progress, transcriptProgress, confirm, ct) =>
        {
            tp = transcriptProgress;
            progress.Report(ImportStage.Transcribe);         // starts the ETA clock at t0
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);          // park; the test drives progress then cancels
            return "s";
        };
        var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3", time: clock);
        decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.RecordedAtText = "2026-03-05 14:30";

        var run = vm.StartCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(vm.IsTranscribing);

        clock.Advance(TimeSpan.FromSeconds(30));
        tp!.Report(new TranscriptionProgress(15000, 60000, "hello world", TranscriptSource.Local));

        Assert.Equal(0.25, vm.TranscribeProgress, 3);        // 15000/60000
        Assert.Contains("25%", vm.TranscribeProgressText);
        // Pin the exact ETA math: 30s elapsed at f=0.25 -> remaining 30s*(1-f)/f = 90s -> "2 min".
        // Stronger than Contains("left"): catches a swapped f/(1-f) or a FormatEta rounding regression.
        Assert.Contains("~2 min left", vm.TranscribeProgressText);
        Assert.Equal("hello world", Assert.Single(vm.PreviewLines));

        vm.CancelCommand.Execute(null);
        await run;
        Assert.False(vm.IsTranscribing);
    }

    [Fact]
    public async Task Progress_below_threshold_shows_percent_only_and_preview_caps_at_ten()
    {
        IProgress<TranscriptionProgress>? tp = null;
        var started = new TaskCompletionSource();
        ImportRunner runner = async (req, progress, transcriptProgress, confirm, ct) =>
        {
            tp = transcriptProgress; progress.Report(ImportStage.Transcribe); started.SetResult();
            await Task.Delay(Timeout.Infinite, ct); return "s";
        };
        var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3");
        decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
        await vm.PickFileCommand.ExecuteAsync(null);
        vm.RecordedAtText = "2026-03-05 14:30";
        var run = vm.StartCommand.ExecuteAsync(null);
        await started.Task;

        tp!.Report(new TranscriptionProgress(500, 60000, "first", TranscriptSource.Local));   // <3%
        Assert.DoesNotContain("left", vm.TranscribeProgressText);
        Assert.Contains("1%", vm.TranscribeProgressText);

        for (int i = 0; i < 12; i++)
            tp.Report(new TranscriptionProgress(1000 * (i + 1), 60000, $"line {i}", TranscriptSource.Local));
        Assert.Equal(10, vm.PreviewLines.Count);             // capped tail
        Assert.Equal("line 11", vm.PreviewLines[^1]);        // newest kept

        vm.CancelCommand.Execute(null); await run;
    }
}

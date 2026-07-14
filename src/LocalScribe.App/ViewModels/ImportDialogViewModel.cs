using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Import;
namespace LocalScribe.App.ViewModels;

/// <summary>The AudioImporter.ImportAsync seam: the window layer passes the real importer's
/// method group; tests pass a fake so the VM is exercised with no FFmpeg/engine on disk.</summary>
public delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress,
    Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);

/// <summary>WPF-free VM behind the plain-Window import dialog (design 2026-07-13 section 4.4):
/// file pick -> probe preview (claims only), editable title (filename stem) and recorded-date
/// (media-creation tag, else the EARLIEST file timestamp; legally meaningful, so user-correctable
/// - it drives the session id/StartedAtUtc), optional matter tagging (the Record-console picker
/// trio), the stereo question when the container claims 2 channels, staged progress with Cancel.
/// The duration-mismatch gate is the injected confirmMismatch seam, passed through to the runner.</summary>
public sealed partial class ImportDialogViewModel : ObservableObject
{
    public const string FileFilter =
        "Audio files (*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg)|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg|All files (*.*)|*.*";
    private const string DateFormat = "yyyy-MM-dd HH:mm";

    private readonly IAudioDecoder _decoder;
    private readonly ImportRunner _runImport;
    private readonly MaintenanceService _maintenance;
    private readonly Func<OpenPathRequest, string?> _pickOpenPath;
    private readonly Func<DurationMismatchInfo, Task<bool>> _confirmMismatch;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly List<LocalScribe.Core.Model.MattersIndexEntry> _allMatters = new();
    private readonly HashSet<string> _pickedMatterIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;

    public ImportDialogViewModel(IAudioDecoder decoder, ImportRunner runImport,
        MaintenanceService maintenance, Func<OpenPathRequest, string?> pickOpenPath,
        Func<DurationMismatchInfo, Task<bool>> confirmMismatch,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)
    {
        (_decoder, _runImport, _maintenance, _pickOpenPath, _confirmMismatch, _errors, _dispatch, _time)
            = (decoder, runImport, maintenance, pickOpenPath, confirmMismatch, errors, dispatch, time);
        PickFileCommand = new AsyncRelayCommand(PickFileAsync, () => !IsBusy);
        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        CancelCommand = new RelayCommand(Cancel);
        ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
    }

    // --- file + probe preview (claims only - decode truth is the importer's job) ---
    [ObservableProperty] private string? _sourcePath;
    [ObservableProperty] private string _fileNameDisplay = "";
    [ObservableProperty] private string _durationDisplay = "";
    [ObservableProperty] private string _sizeDisplay = "";
    [ObservableProperty] private string _formatDisplay = "";
    public bool HasFile => SourcePath is not null;

    // --- editable fields ---
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _recordedAtText = "";
    /// <summary>Null when RecordedAtText parses (or is still empty); else the inline hint.</summary>
    public string? RecordedAtError =>
        RecordedAtText.Trim().Length == 0 || ParseRecordedAt() is not null
            ? null : "Enter the date as " + DateFormat;

    // --- the stereo question (design 4.3), shown only when the container claims 2 channels ---
    [ObservableProperty] private bool _isStereo;
    [ObservableProperty] private bool _eachPartyOwnChannel;
    [ObservableProperty] private bool _swapSides;

    // --- matter picker (the Record-console trio: MatterPickRow / MatterSearch / toggle) ---
    public ObservableCollection<MatterPickRow> MatterOptions { get; } = new();
    [ObservableProperty] private string _matterPickerQuery = "";
    public IRelayCommand<MatterPickRow> ToggleMatterCommand { get; }

    // --- staged progress ---
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _stageText = "";

    public IAsyncRelayCommand PickFileCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public event Action<string>? Completed;
    public event Action? CloseRequested;

    partial void OnTitleChanged(string value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnRecordedAtTextChanged(string value)
    {
        OnPropertyChanged(nameof(RecordedAtError));
        StartCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value)
    {
        PickFileCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
    }
    partial void OnMatterPickerQueryChanged(string value) => RebuildMatterOptions();

    private bool CanStart() => HasFile && !IsBusy && Title.Trim().Length > 0
        && ParseRecordedAt() is not null;

    private DateTimeOffset? ParseRecordedAt()
    {
        if (!DateTime.TryParseExact(RecordedAtText.Trim(), DateFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)) return null;
        // The machine zone's DST-resolved offset AT that historic date - legally meaningful.
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local));
    }

    private async Task PickFileAsync()
    {
        string? path = _pickOpenPath(new OpenPathRequest(FileFilter));
        if (path is null) return;
        try
        {
            var probe = await _decoder.ProbeAsync(path, CancellationToken.None);
            _dispatch(() => Apply(path, probe));
        }
        catch (Exception ex) { _errors.Report("Reading audio file", ex); }
    }

    private void Apply(string path, AudioProbeResult probe)
    {
        SourcePath = path;
        FileNameDisplay = Path.GetFileName(path);
        Title = Path.GetFileNameWithoutExtension(path);
        DurationDisplay = probe.ClaimedDurationMs is long ms ? FormatDuration(ms) : "unknown";
        SizeDisplay = FormatSize(probe.FileSizeBytes);
        FormatDisplay = probe.FormatName.Split(',')[0].ToUpperInvariant();
        IsStereo = probe.ClaimedChannels == 2;
        EachPartyOwnChannel = false;
        SwapSides = false;
        // Recorded-date default (design 4.4): the container's media-creation tag, else the
        // EARLIEST of the file's own timestamps (a copy resets CreationTime; the earlier stamp is
        // the better guess at when the recording happened). Blank when nothing is known.
        DateTimeOffset? recorded = probe.MediaCreatedUtc ?? Earliest(probe.FileCreatedUtc, probe.FileModifiedUtc);
        RecordedAtText = recorded is { } r
            ? TimeZoneInfo.ConvertTime(r, _time.LocalTimeZone).ToString(DateFormat, CultureInfo.InvariantCulture)
            : "";
        OnPropertyChanged(nameof(HasFile));
        StartCommand.NotifyCanExecuteChanged();
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : (a < b ? a : b);

    private async Task StartAsync()
    {
        if (SourcePath is not { } source || ParseRecordedAt() is not { } recordedAt) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var request = new ImportRequest
            {
                SourcePath = source,
                Title = Title.Trim(),
                RecordedAtLocal = recordedAt,
                MatterIds = _pickedMatterIds.ToList(),
                Stereo = !IsStereo || !EachPartyOwnChannel ? StereoMapping.Downmix
                    : SwapSides ? StereoMapping.SplitSwapped : StereoMapping.Split,
            };
            string id = await _runImport(request, new DispatchProgress(this),
                _confirmMismatch, _cts.Token);
            _errors.Info($"Imported \"{request.Title}\".");
            _dispatch(() => { Completed?.Invoke(id); CloseRequested?.Invoke(); });
        }
        catch (OperationCanceledException)
        {
            _errors.Info("Import cancelled - the partial session was discarded; the original file is untouched.");
        }
        catch (Exception ex) { _errors.Report("Import audio", ex); }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            StageText = "";
        }
    }

    /// <summary>Busy: cancel the running import (the importer deletes the partial folder).
    /// Idle: ask the window to close. One button, two safe meanings.</summary>
    private void Cancel()
    {
        if (IsBusy) _cts?.Cancel();
        else CloseRequested?.Invoke();
    }

    /// <summary>Marshals stage reports through _dispatch explicitly (Progress&lt;T&gt; captures a
    /// SynchronizationContext the unit tests do not have).</summary>
    private sealed class DispatchProgress(ImportDialogViewModel owner) : IProgress<ImportStage>
    {
        public void Report(ImportStage value) => owner._dispatch(() => owner.StageText = value switch
        {
            ImportStage.Copy => "Copying original file...",
            ImportStage.Decode => "Decoding audio...",
            ImportStage.Transcribe => "Transcribing...",
            _ => "Saving session...",
        });
    }

    // --- matter picker (mirrors RecordingConsoleViewModel.LoadMattersAsync/Rebuild/Toggle) ---

    /// <summary>Best-effort catalog load (the picker is optional - tag later in Session Details);
    /// a failed read leaves the list empty rather than blocking the import.</summary>
    public async Task LoadMattersAsync()
    {
        try
        {
            var index = await _maintenance.ListMattersAsync(CancellationToken.None);
            _dispatch(() =>
            {
                _allMatters.Clear();
                _allMatters.AddRange(index.Matters.Where(m => !m.Archived));
                RebuildMatterOptions();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadMattersAsync failed: {ex}");
            // Never-silent: the picker is optional (tag later in Session Details), but a broken
            // matter-catalog read must still surface - Info, like every other best-effort path.
            _errors.Info("Couldn't load the matter list; you can tag this session later in Session Details.");
        }
    }

    private void RebuildMatterOptions()
    {
        string q = MatterPickerQuery.Trim();
        MatterOptions.Clear();
        foreach (var e in _allMatters)
            if (q.Length == 0 || MatterSearch.Matches(e, q))
                MatterOptions.Add(new MatterPickRow(e.Id,
                    string.IsNullOrEmpty(e.Reference) ? e.Name : $"{e.Name} ({e.Reference})",
                    _pickedMatterIds.Contains(e.Id)));
    }

    private void ToggleMatter(MatterPickRow? row)
    {
        if (row is null) return;
        if (!_pickedMatterIds.Remove(row.Id)) _pickedMatterIds.Add(row.Id);
        RebuildMatterOptions();
    }

    private static string FormatDuration(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.#} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.#} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0.#} KB",
        _ => $"{bytes} B",
    };
}

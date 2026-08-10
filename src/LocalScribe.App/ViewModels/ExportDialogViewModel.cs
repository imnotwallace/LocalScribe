using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

/// <summary>One entry in the export dialog's cadence dropdown (design 2026-08-04 section 5).
/// A preset list rather than free numeric entry: 1 s puts a stamp on every sentence and 3600 s
/// does nothing, and neither is worth a validation story.</summary>
public sealed record CadenceChoice(int Ms, string Label);

/// <summary>WPF-free VM behind the plain-Window session export dialog (design 3.4). Picks a destination
/// via the injected pickSavePath seam, then runs the MaintenanceService export, surfaces Info/error,
/// reveals the output, and raises Closed on success.</summary>
public sealed partial class ExportDialogViewModel : ObservableObject
{
    private readonly string _sessionId;
    private readonly string _sessionTitle;
    private readonly MaintenanceService _maintenance;
    private readonly ISettingsService _settings;
    private readonly Func<SavePathRequest, string?> _pickSavePath;
    private readonly Action<string> _revealFile;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _cts;

    public ExportDialogViewModel(string sessionId, string sessionTitle, MaintenanceService maintenance,
        ISettingsService settings, Func<SavePathRequest, string?> pickSavePath, Action<string> revealFile,
        IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_sessionId, _sessionTitle, _maintenance, _settings, _pickSavePath, _revealFile, _errors, _dispatch)
            = (sessionId, sessionTitle, maintenance, settings, pickSavePath, revealFile, errors, dispatch);
        // Seed the BACKING FIELDS, not the properties: the generated setters raise
        // PropertyChanged and OnFormatChanged before ExportCommand below exists.
        var e = settings.Current.Export;
        (_format, _includeTimestamps, _includeMarkers, _extraTimestamps, _cadenceIntervalMs,
            _includeSummary, _markCorrectedTurns)
            = (e.Format, e.IncludeTimestamps, e.IncludeMarkers, e.ExtraTimestamps,
               e.CadenceIntervalMs, e.IncludeSummary, e.MarkCorrectedTurns);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
    }

    [ObservableProperty] private ExportFormat _format = ExportFormat.Zip;
    [ObservableProperty] private bool _includeTimestamps = true;
    [ObservableProperty] private bool _includeMarkers = true;
    [ObservableProperty] private bool _extraTimestamps;
    [ObservableProperty] private bool _includeSummary;
    /// <summary>Flag rewritten turns (Tier 1 T1-8). Default ON, unlike IncludeSummary: a summary is
    /// an ADDITION the user opts into, this is a DISCLOSURE about content already in the document.</summary>
    [ObservableProperty] private bool _markCorrectedTurns = true;
    [ObservableProperty] private bool _isBusy;

    /// <summary>Dialog-local feedback, bound to THIS window's own InfoBar (Tier 1 plan D, T1-5,
    /// 2026-08-05, copying the SplitSpeakersWindow shape): the shared IUiErrorReporter renders on
    /// MainWindow's InfoBar, which this separate dialog cannot show - so a range-validation
    /// refusal or a failed write looked completely silent here. Null = no status; cleared at the
    /// start of every export attempt. The shell reporter is still called as well: the dialog
    /// CLOSES on success, so the success notice needs somewhere that outlives it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    /// <summary>True renders the status InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The status InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// cannot bind a null-check directly).</summary>
    public bool HasStatus => StatusMessage is not null;

    /// <summary>Public because ExportDialogStatusTests seeds a stale status to prove the next
    /// attempt clears it (there is no InternalsVisibleTo in this repo).</summary>
    public void ShowStatus(string message, bool isError) =>
        _dispatch(() => { StatusMessage = message; StatusIsError = isError; });

    private void ClearStatus() =>
        _dispatch(() => { StatusMessage = null; StatusIsError = false; });

    /// <summary>Stops an export in flight (Tier 1 plan D, T1-5). Before this, all four export
    /// calls passed CancellationToken.None, so a multi-gigabyte .zip of a long call could not be
    /// stopped at all. MaintenanceService.ExportWithOutputCleanupAsync already deletes the
    /// partially written output on failure, so a cancelled export leaves no half file behind.</summary>
    public IRelayCommand StopCommand { get; }

    /// <summary>Time-range excerpt (design 2026-08-04 section 8). NEVER seeded from settings and
    /// never persisted: a remembered range would silently emit a partial export of the next,
    /// unrelated session.</summary>
    [ObservableProperty] private bool _excerptEnabled;
    [ObservableProperty] private string _excerptFrom = "";
    [ObservableProperty] private string _excerptTo = "";

    /// <summary>Timestamps are the anchor that maps an excerpt back to the full transcript - line
    /// numbers restart within the excerpt and do not - so an excerpt forces them on.</summary>
    public bool TimestampsToggleEnabled => !ExcerptEnabled;

    /// <summary>What the "Include timestamps" checkbox DISPLAYS and toggles - deliberately NOT the
    /// same thing as IncludeTimestamps itself (review finding 2026-08-04: the original
    /// OnExcerptEnabledChanged forced the real, PERSISTED IncludeTimestamps to true, so any export
    /// that followed - any format, whether or not the excerpt was still on - silently flipped the
    /// user's saved preference in settings.json). An excerpt visually forces this checkbox on
    /// without touching IncludeTimestamps: PersistChoicesAsync keeps saving what the user actually
    /// picked, and ExportAsync forces the EXPORT's own timestamps on independently, at the
    /// ExportOptions build. The checkbox is also disabled via TimestampsToggleEnabled while an
    /// excerpt is active, so the setter only ever runs while ExcerptEnabled is false, when this is
    /// a plain passthrough to IncludeTimestamps.</summary>
    public bool TimestampsChecked
    {
        get => IncludeTimestamps || ExcerptEnabled;
        set { if (!ExcerptEnabled) IncludeTimestamps = value; }
    }

    partial void OnExcerptEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(TimestampsToggleEnabled));
        OnPropertyChanged(nameof(TimestampsChecked));
    }

    partial void OnIncludeTimestampsChanged(bool value) => OnPropertyChanged(nameof(TimestampsChecked));

    public IReadOnlyList<CadenceChoice> CadenceChoices { get; } =
        [new(10000, "10 s"), new(15000, "15 s"), new(30000, "30 s"), new(60000, "60 s")];

    [ObservableProperty] private int _cadenceIntervalMs = 15000;

    /// <summary>What the dropdown shows and sets. Reading snaps a non-preset settings.json value
    /// to the nearest preset for DISPLAY only - CadenceIntervalMs keeps the loaded value until the
    /// user actually picks one (design 2026-08-04 section 5). Writing replaces it outright.</summary>
    public int SelectedCadenceMs
    {
        get => CadenceChoices.Any(c => c.Ms == CadenceIntervalMs)
            ? CadenceIntervalMs
            : CadenceChoices.MinBy(c => Math.Abs(c.Ms - CadenceIntervalMs))!.Ms;
        set { CadenceIntervalMs = value; OnPropertyChanged(); }
    }

    public bool IsDocx => Format == ExportFormat.Docx;
    /// <summary>The IncludeTimestamps/IncludeMarkers/ExtraTimestamps checkboxes apply to ALL
    /// THREE textual formats (design 2026-07-18 section 3 + 2026-08-04 section 3) - docx, markdown
    /// AND plain text; hidden for zip, which archives the session folder as-is. This generalizes
    /// the old IsDocx visibility gate (kept above, unbroken).</summary>
    public bool ShowOptionToggles =>
        Format is ExportFormat.Docx or ExportFormat.Markdown or ExportFormat.Text;
    partial void OnFormatChanged(ExportFormat value)
    {
        OnPropertyChanged(nameof(IsDocx));
        OnPropertyChanged(nameof(ShowOptionToggles));
    }
    partial void OnIsBusyChanged(bool value)
    {
        ExportCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand ExportCommand { get; }
    public event Action? Closed;

    private async Task ExportAsync()
    {
        ClearStatus();
        // One CTS per attempt, disposed in the using: reusing a cancelled source would make every
        // later export in this same dialog start already-cancelled.
        using var cts = new CancellationTokenSource();
        _cts = cts;
        CancellationToken ct = cts.Token;
        IsBusy = true;
        try
        {
            // Resolved BEFORE the Save-As build so a bad range is reported before the user picks a
            // destination. The range applies only to the three textual formats; Zip's range
            // controls are hidden, and the archive has no rows to select from.
            ExcerptRange? excerpt = null;
            if (ExcerptEnabled && Format != ExportFormat.Zip)
            {
                // Fix 4 (whole-branch review): ResolveExcerptAsync treats BOTH bounds blank as
                // the full range [0, durationMs] - correct for a caller that WANTS the whole
                // transcript, but here the user ticked "Export a time range only" and typed
                // nothing. Letting that through would stamp EXCERPT on a document that is
                // actually complete, plus a "-excerpt" filename suffix, for no reason. Checked
                // here, ahead of both the service call and the Save-As picker, so the error
                // surfaces through the same catch/_errors.Report path as every other range
                // validation failure (see A_bad_range_is_reported_before_the_save_as_picker_opens).
                if (string.IsNullOrWhiteSpace(ExcerptFrom) && string.IsNullOrWhiteSpace(ExcerptTo))
                    throw new InvalidOperationException(
                        "Enter a start or end time for the excerpt, or turn off the time-range option.");
                excerpt = await _maintenance.ResolveExcerptAsync(_sessionId, ExcerptFrom, ExcerptTo, ct);
            }

            SavePathRequest request;
            if (Format == ExportFormat.Zip)
            {
                // The .zip is the raw session folder; it keeps its session-id name so the default
                // template reproduces every pre-Round-2 filename byte-for-byte.
                request = new SavePathRequest(_sessionId + ".zip", "Zip archive (*.zip)|*.zip");
            }
            else
            {
                var tokens = await _maintenance.FilenameTokensAsync(_sessionId, ct);
                string stem = ExportFileNames.Sanitize(
                    ExportFileNames.Expand(_settings.Current.Export.FilenameTemplate, tokens));
                // Forced, outside template control: a file named identically to the full transcript
                // is precisely how an excerpt gets filed as one.
                if (excerpt is not null) stem += "-excerpt";
                (string ext, string filter) = Format switch
                {
                    ExportFormat.Markdown => (".md", "Markdown (*.md)|*.md"),
                    ExportFormat.Text => (".txt", "Plain text (*.txt)|*.txt"),
                    _ => (".docx", "Word document (*.docx)|*.docx"),
                };
                request = new SavePathRequest(stem + ext, filter);
            }
            string? dest = _pickSavePath(request);
            if (string.IsNullOrWhiteSpace(dest)) return;                  // user cancelled Save-As

            // One options build for ALL THREE textual formats - the checkboxes mean the same thing.
            // The cadence rides IncludeTimestamps: unchecking timestamps forces the interval off
            // even while the (disabled) cadence checkbox is still ticked. An excerpt forces
            // timestamps for THIS EXPORT ONLY (review finding 2026-08-04) - IncludeTimestamps
            // itself, the persisted preference, is read but never written here.
            bool exportTimestamps = IncludeTimestamps || excerpt is not null;
            var options = new ExportOptions
            {
                IncludeTimestamps = exportTimestamps, IncludeMarkers = IncludeMarkers,
                TimestampIntervalMs = exportTimestamps && ExtraTimestamps ? CadenceIntervalMs : 0,
                IncludeSummary = IncludeSummary,
                MarkCorrectedTurns = MarkCorrectedTurns,
            };
            switch (Format)
            {
                case ExportFormat.Zip:
                    await _maintenance.ExportSessionArchiveAsync(_sessionId, dest, ct);
                    break;
                case ExportFormat.Markdown:
                    await _maintenance.ExportMarkdownAsync(_sessionId, dest, options, excerpt, ct);
                    break;
                case ExportFormat.Text:
                    await _maintenance.ExportTextAsync(_sessionId, dest, options, excerpt, ct);
                    break;
                default:
                    await _maintenance.ExportDocxAsync(_sessionId, dest, options, excerpt, ct);
                    break;
            }
            _errors.Info("Exported to " + dest, NoticeSeverity.Success);
            _revealFile(dest);
            await PersistChoicesAsync();
            _dispatch(() => Closed?.Invoke());
        }
        catch (OperationCanceledException)
        {
            // The user pressed Stop. NOT a failure: no red bar and no shell Report (the
            // ImportDialogViewModel precedent). The partial output file is already gone -
            // MaintenanceService.ExportWithOutputCleanupAsync deletes what it created.
            ShowStatus("Export cancelled - no file was written.", isError: false);
        }
        catch (Exception ex)
        {
            // BOTH surfaces: the dialog stays open on failure, so the bar the user is looking at
            // must carry the reason; the shell queue keeps it after the dialog is dismissed and
            // is where Plan A's diagnostic log picks it up.
            ShowStatus(ex.Message, isError: true);
            _errors.Report("Export", ex);
        }
        finally
        {
            _cts = null;
            IsBusy = false;
        }
    }

    /// <summary>Remember what the user last ACTUALLY did (design 2026-08-04 section 4): called
    /// only after a successful export, never on dialog-open and never on cancel. A save failure is
    /// reported but must never fail an export that already succeeded, so this is awaited AFTER the
    /// success Info and the reveal.</summary>
    private async Task PersistChoicesAsync()
    {
        try
        {
            await _settings.SaveAsync(_settings.Current with
            {
                Export = _settings.Current.Export with
                {
                    Format = Format,
                    IncludeTimestamps = IncludeTimestamps,
                    IncludeMarkers = IncludeMarkers,
                    ExtraTimestamps = ExtraTimestamps,
                    CadenceIntervalMs = CadenceIntervalMs,
                    IncludeSummary = IncludeSummary,
                    MarkCorrectedTurns = MarkCorrectedTurns,
                },
            }, CancellationToken.None);
        }
        catch (Exception ex) { _errors.Report("Saving export choices", ex); }
    }
}

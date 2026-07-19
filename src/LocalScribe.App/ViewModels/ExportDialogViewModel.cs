using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

public enum ExportFormat { Zip, Docx, Markdown }

/// <summary>WPF-free VM behind the plain-Window session export dialog (design 3.4). Picks a destination
/// via the injected pickSavePath seam, then runs the MaintenanceService export, surfaces Info/error,
/// reveals the output, and raises Closed on success.</summary>
public sealed partial class ExportDialogViewModel : ObservableObject
{
    private readonly string _sessionId;
    private readonly string _sessionTitle;
    private readonly MaintenanceService _maintenance;
    private readonly Func<SavePathRequest, string?> _pickSavePath;
    private readonly Action<string> _revealFile;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;

    public ExportDialogViewModel(string sessionId, string sessionTitle, MaintenanceService maintenance,
        Func<SavePathRequest, string?> pickSavePath, Action<string> revealFile,
        IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_sessionId, _sessionTitle, _maintenance, _pickSavePath, _revealFile, _errors, _dispatch)
            = (sessionId, sessionTitle, maintenance, pickSavePath, revealFile, errors, dispatch);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => !IsBusy);
    }

    [ObservableProperty] private ExportFormat _format = ExportFormat.Zip;
    [ObservableProperty] private bool _includeTimestamps = true;
    [ObservableProperty] private bool _includeMarkers = true;
    [ObservableProperty] private bool _isBusy;

    public bool IsDocx => Format == ExportFormat.Docx;
    /// <summary>The dialog's IncludeTimestamps/IncludeMarkers checkboxes apply to BOTH textual
    /// formats (design 2026-07-18 section 3) - this generalizes the old IsDocx visibility gate
    /// (kept above, unbroken) for the XAML toggle panel.</summary>
    public bool ShowOptionToggles => Format is ExportFormat.Docx or ExportFormat.Markdown;
    partial void OnFormatChanged(ExportFormat value)
    {
        OnPropertyChanged(nameof(IsDocx));
        OnPropertyChanged(nameof(ShowOptionToggles));
    }
    partial void OnIsBusyChanged(bool value) => ExportCommand.NotifyCanExecuteChanged();

    public IAsyncRelayCommand ExportCommand { get; }
    public event Action? Closed;

    private async Task ExportAsync()
    {
        var request = Format switch
        {
            ExportFormat.Zip => new SavePathRequest(_sessionId + ".zip", "Zip archive (*.zip)|*.zip"),
            ExportFormat.Markdown => new SavePathRequest(
                ExportFileNames.Sanitize(_sessionTitle) + ".md", "Markdown (*.md)|*.md"),
            _ => new SavePathRequest(
                ExportFileNames.Sanitize(_sessionTitle) + ".docx", "Word document (*.docx)|*.docx"),
        };
        string? dest = _pickSavePath(request);
        if (string.IsNullOrWhiteSpace(dest)) return;                  // user cancelled Save-As

        IsBusy = true;
        try
        {
            // One options build for both textual formats - the checkboxes mean the same thing.
            var options = new DocxOptions
            { IncludeTimestamps = IncludeTimestamps, IncludeMarkers = IncludeMarkers };
            switch (Format)
            {
                case ExportFormat.Zip:
                    await _maintenance.ExportSessionArchiveAsync(_sessionId, dest, CancellationToken.None);
                    break;
                case ExportFormat.Markdown:
                    await _maintenance.ExportMarkdownAsync(_sessionId, dest, options, CancellationToken.None);
                    break;
                default:
                    await _maintenance.ExportDocxAsync(_sessionId, dest, options, CancellationToken.None);
                    break;
            }
            _errors.Info("Exported to " + dest);
            _revealFile(dest);
            _dispatch(() => Closed?.Invoke());
        }
        catch (Exception ex) { _errors.Report("Export", ex); }
        finally { IsBusy = false; }
    }
}

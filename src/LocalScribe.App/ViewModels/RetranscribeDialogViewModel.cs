using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
namespace LocalScribe.App.ViewModels;

/// <summary>WPF-free VM behind the plain-Window Re-transcribe dialog (design 2026-07-13 section
/// 3.4). Model picker = CANONICAL names of models actually on disk - ModelPaths.AvailableModels
/// collapses quantized ggml files (ggml-{name}-q8_0.bin) to their canonical name and
/// ModelFileResolver picks the file per backend at engine creation - and never "auto" (an
/// explicit re-run should be an explicit choice); language defaults to auto-detect. Start hands the run to the SHARED
/// RetranscriptionRunner and the dialog may close while it runs - completion lands on the
/// app-level reporter, the row chip rides the runner events, and Cancel works from any later
/// dialog instance because the cancellation lives in the runner, not here.</summary>
public sealed partial class RetranscribeDialogViewModel : ObservableObject, IDisposable
{
    private readonly string _sessionId;
    private readonly MaintenanceService _maintenance;
    private readonly RetranscriptionRunner _runner;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private bool _disposed;

    public RetranscribeDialogViewModel(string sessionId, MaintenanceService maintenance,
        RetranscriptionRunner runner, Func<IReadOnlySet<string>> availableModels,
        IUiErrorReporter errors, Action<Action> dispatch)
    {
        (_sessionId, _maintenance, _runner, _errors, _dispatch)
            = (sessionId, maintenance, runner, errors, dispatch);
        // availableModels = ModelPaths.AvailableModels in production: CANONICAL model names
        // (quantized files collapse via ModelFileResolver.CanonicalName), so every pick here is
        // a name BackendSelector.Select accepts and the runner's presence gate recognizes.
        ModelChoices = availableModels().OrderBy(m => m, StringComparer.Ordinal).ToList();
        // Commands must exist BEFORE SelectedModel/IsRunning are assigned below: those are real
        // [ObservableProperty] setters (not field initializers), so they invoke
        // OnSelectedModelChanged/OnIsRunningChanged synchronously, which call
        // StartCommand/CancelRunCommand.NotifyCanExecuteChanged() - constructing the commands
        // first avoids a null-reference on that first assignment.
        StartCommand = new AsyncRelayCommand(StartAsync, () => SelectedModel is not null && !IsRunning);
        CancelRunCommand = new RelayCommand(_runner.CancelCurrent, () => IsRunning);
        SelectedModel = ModelChoices.FirstOrDefault();
        // F3 fix (whole-branch review): gate on THIS dialog's own session, not "some session is
        // running" globally - otherwise a dialog opened for session B would show IsRunning=true
        // and enable Cancel while session A's (unrelated) run is in flight, and clicking Cancel
        // would cancel A's run (RetranscriptionRunner.CancelCurrent has no session scoping of its
        // own - it always cancels whatever is currently running).
        IsRunning = runner.RunningSessionId == sessionId;
        // A run started from ANOTHER dialog instance (or settling while this one is open) must
        // flip the gates here too. Named handlers so Dispose can detach - the runner is
        // app-lifetime and must not root closed dialogs.
        _runner.RetranscriptionStarted += OnRunnerActivity;
        _runner.RetranscriptionCompleted += OnRunnerActivity;
    }

    public IReadOnlyList<string> ModelChoices { get; }
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } = LanguageChoice.All;

    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private string _language = "auto";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _currentVersionDisplay = "";

    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelRunCommand { get; }
    /// <summary>Raised (dispatched) only on SUCCESS - the window closes itself; refusals and
    /// faults leave the dialog open with the reason on the reporter.</summary>
    public event Action? Closed;

    partial void OnSelectedModelChanged(string? value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnIsRunningChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        CancelRunCommand.NotifyCanExecuteChanged();
    }

    // F3 fix (whole-branch review): see the ctor's IsRunning assignment doc for why this must
    // compare against _sessionId rather than testing RunningSessionId for null.
    private void OnRunnerActivity(string _)
        => _dispatch(() => IsRunning = _runner.RunningSessionId == _sessionId);

    /// <summary>The "Current: vN - model - date" info line (design section 3.4).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(_sessionId, ct);
            if (item is null) return;
            var s = item.Session;
            var active = s.Versions.FirstOrDefault(v => v.Id == s.ActiveVersion);
            string line = active is null
                ? $"Current transcript: v1 \u00B7 {s.Model} \u00B7 {s.Backend}"
                : $"Current transcript: {TranscriptVersions.ShortId(active.Id)} \u00B7 {active.Model} "
                  + $"\u00B7 {active.CreatedAtUtc:yyyy-MM-dd}";
            _dispatch(() => CurrentVersionDisplay = line);
        }
        catch (Exception ex) { _errors.Report("Load session versions", ex); }
    }

    private async Task StartAsync()
    {
        string model = SelectedModel!;
        IsRunning = true;
        try
        {
            // Task.Run: the run is CPU-heavy (decode + whisper) and this VM's dispatch is the UI
            // thread; the runner owns cancellation (CancelCurrent), so no token is passed here.
            string? versionId = await Task.Run(() => _runner.RunAsync(new RetranscriptionRequest
            { SessionId = _sessionId, Model = model, Language = Language }, CancellationToken.None));
            if (versionId is not null)
            {
                _errors.Info($"Re-transcription complete - {TranscriptVersions.ShortId(versionId)} "
                    + "is now the active transcript.");
                _dispatch(() => Closed?.Invoke());
            }
            // null = refused: the runner already raised the reason through its Notice wiring.
        }
        catch (OperationCanceledException)
        {
            _errors.Info("Re-transcription cancelled - the partial version was discarded; "
                + "the session is unchanged.");
        }
        catch (Exception ex) { _errors.Report("Re-transcribe", ex); }
        finally { IsRunning = _runner.RunningSessionId == _sessionId; }
    }

    /// <summary>Detaches the runner subscriptions (the only external-object subscriptions this
    /// VM makes) - same leak rule as MetadataEditorViewModel.Dispose. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runner.RetranscriptionStarted -= OnRunnerActivity;
        _runner.RetranscriptionCompleted -= OnRunnerActivity;
    }
}

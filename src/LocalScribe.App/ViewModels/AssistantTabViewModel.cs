using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;

namespace LocalScribe.App.ViewModels;

/// <summary>Session Details "Assistant" tab (design 2026-07-18 section 7.6): summary render
/// with version switcher, stale badge, explicit Regenerate CTA (never automatic), streaming,
/// the VISIBLE queued-behind-recording state, visible errors (7.7), and the locked
/// AI-generated-draft label. WPF-free; dispatch injected (house rule - never Progress&lt;T&gt;).</summary>
public sealed partial class AssistantTabViewModel : ObservableObject
{
    private readonly SummarizationService _summarizer;
    private readonly SummaryStore _store;
    private readonly AssistantManifestCache _models;
    private readonly ISettingsService _settings;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    /// <summary>Resolves the deployed helper exe (null = not deployed). Injected for tests;
    /// production uses AssistantHelperLocator.FindExe (design 2026-07-23 section 4).</summary>
    private readonly Func<string?> _helperProbe;
    private string _sessionId = "";

    public AssistantTabViewModel(SummarizationService summarizer, SummaryStore store,
        AssistantManifestCache models, ISettingsService settings, IUiErrorReporter errors,
        Action<Action> dispatch, Func<string?>? helperProbe = null)
    {
        (_summarizer, _store, _models, _settings, _errors, _dispatch) =
            (summarizer, store, models, settings, errors, dispatch);
        _helperProbe = helperProbe ?? AssistantHelperLocator.FindExe;
        RegenerateCommand = new AsyncRelayCommand(RegenerateAsync, () => AssistantAvailable && !IsRunning);
    }

    /// <summary>The LOCKED artifact label, rendered above every summary (evidentiary rule).</summary>
    public string DraftLabel => AssistantPrompts.DraftLabel;

    /// <summary>Newest first; the switcher keeps every old version readable (append-only store).</summary>
    public ObservableCollection<SummaryVersion> Versions { get; } = [];

    [ObservableProperty] private SummaryVersion? _selectedVersion;
    [ObservableProperty] private string _contentText = "";
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _versionInfo = "";
    [ObservableProperty] private bool _hasSummary;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _waitingText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _streamText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _assistantAvailable;
    [ObservableProperty] private string _disabledExplainer = "";

    public IAsyncRelayCommand RegenerateCommand { get; }

    partial void OnSelectedVersionChanged(SummaryVersion? value)
    {
        ContentText = value?.ContentMarkdown ?? "";
        IsStale = value?.Stale ?? false;
        HasSummary = value is not null;
        VersionInfo = value is null ? "" : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{value.Id} \u00B7 {value.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} \u00B7 {value.Model.File} ({value.Model.Backend.ToUpperInvariant()}{(value.CudaFellToCpu ? " - GPU unavailable, fell to CPU" : "")}) \u00B7 transcript {value.SourceTranscriptVersion}");
    }

    partial void OnIsRunningChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();
    partial void OnAssistantAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();

    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        _sessionId = sessionId;
        try
        {
            bool enabled = _settings.Current.Assistant.Enabled;
            var manifest = enabled ? await Task.Run(() => _models.GetAsync(ct), ct) : null;
            var versions = await _store.LoadAsync(sessionId, ct);
            _dispatch(() =>
            {
                string? helper = _helperProbe();
                AssistantAvailable = enabled && manifest is { Installed.Count: > 0 } && helper is not null;
                // Design 2026-07-23 section 4: model and helper are DISTINCT failures; when both
                // are missing both explainers show, so fixing one cannot hide the other.
                DisabledExplainer = !enabled
                    ? "The assistant is turned off in Settings."
                    : string.Join(" ", new[]
                      {
                          manifest is { Installed.Count: 0 }
                              ? "No assistant model is installed - see Settings > Assistant for fetch instructions."
                              : null,
                          helper is null ? AssistantHelperLocator.MissingMessage : null,
                      }.Where(s => s is not null));
                Versions.Clear();
                foreach (var v in versions.Reverse()) Versions.Add(v);   // newest first
                SelectedVersion = Versions.FirstOrDefault();
            });
        }
        catch (Exception ex) { _errors.Report("Loading assistant state", ex); }
    }

    private async Task RegenerateAsync()
    {
        IsRunning = true;
        ErrorText = ""; WaitingText = ""; PhaseText = ""; StreamText = "";
        try
        {
            var v = await Task.Run(() => _summarizer.SummarizeAsync(_sessionId,
                evt => _dispatch(() => OnJobEvent(evt)),
                reason => _dispatch(() => WaitingText = reason),   // VISIBLY queued (7.1/7.7)
                CancellationToken.None));
            _dispatch(() => { Versions.Insert(0, v); SelectedVersion = v; });
        }
        catch (AssistantException ex) { ErrorText = ex.Message; }   // visible, nothing persisted (7.7)
        catch (OperationCanceledException)
        {
            // A recording started and preempted this draft (design 7.1). Nothing was persisted; the user can retry later.
            ErrorText = "Summary canceled - a recording started. You can regenerate it after recording.";
        }
        catch (Exception ex) { _errors.Report("Generating summary", ex); ErrorText = ex.Message; }
        finally { IsRunning = false; WaitingText = ""; PhaseText = ""; StreamText = ""; }
    }

    private void OnJobEvent(AssistantEvent evt)
    {
        switch (evt)
        {
            case AssistantChunk c:
                StreamText += c.Text;
                WaitingText = "";
                break;
            case AssistantProgress p:
                // The raw wire phase is never what the user reads (design 2026-07-23 section 7).
                PhaseText = p.Phase == AssistantWire.CudaFellPhase
                    ? "GPU unavailable - continuing on CPU"
                    : p.Total > 0 ? $"{p.Phase} {p.Current}/{p.Total}" : p.Phase;
                WaitingText = "";
                break;
        }
    }
}

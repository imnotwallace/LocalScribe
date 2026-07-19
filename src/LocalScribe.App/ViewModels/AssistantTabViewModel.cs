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
    private string _sessionId = "";

    public AssistantTabViewModel(SummarizationService summarizer, SummaryStore store,
        AssistantManifestCache models, ISettingsService settings, IUiErrorReporter errors,
        Action<Action> dispatch)
    {
        (_summarizer, _store, _models, _settings, _errors, _dispatch) =
            (summarizer, store, models, settings, errors, dispatch);
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
            $"{value.Id} \u00B7 {value.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} \u00B7 {value.Model.File} ({value.Model.Backend.ToUpperInvariant()}) \u00B7 transcript {value.SourceTranscriptVersion}");
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
                AssistantAvailable = enabled && manifest is { Installed.Count: > 0 };
                DisabledExplainer = !enabled
                    ? "The assistant is turned off in Settings."
                    : manifest is { Installed.Count: 0 }
                        ? "No assistant model is installed - see Settings > Assistant for fetch instructions."
                        : "";
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
                PhaseText = p.Total > 0 ? $"{p.Phase} {p.Current}/{p.Total}" : p.Phase;
                WaitingText = "";
                break;
        }
    }
}

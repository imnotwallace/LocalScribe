using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>Idle-state brains of the Record console (design 5.4 section 6): a settings-derived
/// summary of what Start WILL capture, plus the per-session target-app selector that seeds from
/// Settings.Remote.App and mirrors into RemoteAppOverride - never into settings.json. All
/// lifecycle state/commands stay on the shared SessionViewModel (locked decision 1: no new
/// lifecycle logic; this VM only composes it). WPF-free; settings.Changed carries no thread
/// contract, so its handler marshals through the injected dispatch.</summary>
public sealed partial class RecordingConsoleViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly RemoteAppOverride _remoteOverride;
    private readonly Action<Action> _dispatch;

    public SessionViewModel Session { get; }

    /// <summary>The console selector's text: the app to record for THIS session. Seeds from
    /// Settings.Remote.App; re-seeds when a session ends (next session reverts to the saved
    /// default) and when a settings save changes the default under an untouched selector.</summary>
    [ObservableProperty] private string _sessionTargetApp = "";

    public bool ShowAppSelector => _settings.Current.Remote.Mode == RemoteMode.PerProcess;
    public IReadOnlyList<string> AppSuggestions { get; } = RemoteCapturePlanner.SuggestedPerProcessApps;

    public string RemoteSummary
    {
        get
        {
            var remote = _settings.Current.Remote;
            if (remote.Mode == RemoteMode.SystemMix) return "Remote audio: full system mix";
            if (remote.Mode == RemoteMode.PerProcess)
            {
                string target = SessionTargetApp.Trim();
                return target.Length > 0
                    ? $"Remote audio: per-app ({target})"
                    : "Remote audio: per-app (no app set - will fall back to system mix)";
            }
            return "Remote audio: auto (Webex/Zoom per-app when found, else system mix)";
        }
    }

    public string MicSummary => _settings.Current.Mic.Mode == MicMode.Pinned
        ? "Microphone: pinned - " + (_settings.Current.Mic.Name ?? "(unnamed device)")
        : "Microphone: follows the Windows Communications default";

    public RecordingConsoleViewModel(ISettingsService settings, SessionViewModel session,
        RemoteAppOverride remoteOverride, Action<Action> dispatch)
    {
        (_settings, Session, _remoteOverride, _dispatch) = (settings, session, remoteOverride, dispatch);
        _sessionTargetApp = settings.Current.Remote.App ?? "";
        _remoteOverride.App = Normalize(_sessionTargetApp);
        settings.Changed += OnSettingsChanged;
        session.PropertyChanged += OnSessionChanged;
    }

    private static string? Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    partial void OnSessionTargetAppChanged(string value)
    {
        _remoteOverride.App = Normalize(value);
        OnPropertyChanged(nameof(RemoteSummary));
    }

    // A finished session reverts the selector (and thus the override) to the saved default:
    // the override is per-session by construction, not by cleanup code elsewhere.
    private void OnSessionChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.State)) return;
        if (Session.State == SessionState.Idle)
            SessionTargetApp = _settings.Current.Remote.App ?? "";
    }

    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
        => _dispatch(() =>
        {
            // Re-seed only an UNTOUCHED selector (still equal to the old default): a user's
            // in-flight per-session edit is never clobbered by a background settings save.
            if (SessionTargetApp == (oldSettings.Remote.App ?? ""))
                SessionTargetApp = newSettings.Remote.App ?? "";
            OnPropertyChanged(nameof(ShowAppSelector));
            OnPropertyChanged(nameof(RemoteSummary));
            OnPropertyChanged(nameof(MicSummary));
        });

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Session.PropertyChanged -= OnSessionChanged;
    }
}

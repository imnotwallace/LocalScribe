using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.App;

/// <summary>The Split-speakers dialog window (design section 4): one per invocation - opened
/// fresh from the read view's button or the sessions-page context menu, genuinely closed (not a
/// hide-on-close singleton). Capture-excluded per settings.Privacy - like every transcript-bearing
/// window (this dialog previews utterances per cluster) - via the shared CaptureExclusion.Apply
/// helper, applied in OnSourceInitialized exactly as ReadViewWindow/LiveViewWindow do (that is the
/// first point the HWND exists). Owns a private MediaPlayerDualAudioPlayer to satisfy the VM's
/// PlaySnippet hook: seeks the relevant leg to the cluster's snippet start and mutes the other leg
/// so only the requested source is audible.
///
/// KNOWN GAP (noted, not fixed here - out of Task 9's stated scope): unlike ReadViewWindow, this
/// window does not register with WindowRegistry, so a session delete initiated while this dialog
/// is open does not close it first; WindowRegistry's contract is "one window per session" and
/// registering here would silently evict an already-open ReadViewWindow's close action for the
/// same session id. See the Task 9 report for the follow-up recommendation.</summary>
public partial class SplitSpeakersWindow
{
    private readonly SplitSpeakersViewModel _vm;
    private readonly string _sessionId;
    private readonly ISettingsService _settings;
    private readonly MediaPlayerDualAudioPlayer _player = new();
    private bool _hwndReady;

    public SplitSpeakersWindow(SplitSpeakersViewModel vm, string sessionId, ISettingsService settings)
    {
        InitializeComponent();
        (_vm, _sessionId, _settings) = (vm, sessionId, settings);
        DataContext = vm;
        _settings.Changed += OnSettingsChanged;
        _vm.PlaySnippet = PlaySnippetAsync;

        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            LoadPlayerLegs();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);
    }

    // ISettingsService.Changed carries no thread contract; marshal to the UI thread before
    // touching the HWND. _hwndReady guards a save landing before the window was first shown.
    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
    {
        if (!CaptureExclusionPolicy.ShouldReapply(oldSettings, newSettings)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwndReady)
                CaptureExclusion.Apply(this, newSettings.Privacy.ExcludeWindowsFromCapture);
        });
    }

    // Loads whichever leg(s) the loaded session actually offered as splittable (Sources), so the
    // play button can seek+mute per cluster without re-probing the filesystem itself.
    private void LoadPlayerLegs()
    {
        string? local = _vm.Sources.FirstOrDefault(s => s.Source == SourceKind.Local)?.LegPath;
        string? remote = _vm.Sources.FirstOrDefault(s => s.Source == SourceKind.Remote)?.LegPath;
        if (local is not null || remote is not null) _player.Load(local, remote);
    }

    // The VM's PlaySnippet hook (design 4.2): play only the cluster's own source leg, muting the
    // other leg so background audio from the other side does not compete with the preview.
    private Task PlaySnippetAsync(SourceKind source, long startMs)
    {
        _player.SetLegMuted(local: true, muted: source != SourceKind.Local);
        _player.SetLegMuted(local: false, muted: source != SourceKind.Remote);
        _player.SeekMs(startMs);
        _player.Play();
        return Task.CompletedTask;
    }

    private void OnPlaySnippet(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClusterRowViewModel row } && _vm.PlaySnippet is { } play)
            _ = play(row.Source, row.SnippetStartMs ?? 0);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _settings.Changed -= OnSettingsChanged;
        _player.Dispose();
        base.OnClosed(e);
    }
}

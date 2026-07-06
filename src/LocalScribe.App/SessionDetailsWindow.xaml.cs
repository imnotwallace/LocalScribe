using System;
using System.Threading;
using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;

namespace LocalScribe.App;

/// <summary>Stage 5.2 Session Details window: the session metadata editor as a dedicated,
/// reusable window (opened by id from Sessions "Open detail" and Matters). Genuinely closable;
/// re-created per open. Copies ReadViewWindow's registry/capture-exclusion/window-state
/// lifecycle. Hosts a per-window MetadataEditorViewModel (no shared editor state). Placement
/// uses the simple MainWindow-style Load+clamp restore (one shared "sessionDetailsDefault" key)
/// rather than ReadViewWindow's cascade helper: unlike read views (routinely opened several at
/// once side by side), Session Details is opened one at a time per user action, so a cascade
/// offset isn't worth the extra OpenCount bookkeeping. Stage 5.4 5.1: the 250 ms Tick timer is
/// gone with the transient Saved toast - the persistent dirty indicator is plain bound state.</summary>
public partial class SessionDetailsWindow
{
    private readonly MetadataEditorViewModel _vm;
    private readonly string _sessionId;
    private readonly WindowRegistry _registry;
    private readonly WindowStateStore _stateStore;
    private readonly ISettingsService _settings;
    private bool _hwndReady;

    public SessionDetailsWindow(MetadataEditorViewModel vm, string sessionId, WindowRegistry registry,
        WindowStateStore stateStore, ISettingsService settings)
    {
        InitializeComponent();
        (_vm, _sessionId, _registry, _stateStore, _settings) = (vm, sessionId, registry, stateStore, settings);
        DataContext = vm;
        registry.Register(sessionId, Close);
        // Re-apply capture exclusion when Privacy.ExcludeWindowsFromCapture is toggled while this
        // details window is open, mirroring ReadViewWindow/MainWindow. This is a per-session
        // window that genuinely closes, so OnClosed MUST unsubscribe.
        _settings.Changed += OnSettingsChanged;
        Loaded += async (_, _) => await _vm.LoadAsync(_sessionId, CancellationToken.None);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);
        if (_stateStore.Load("sessionDetailsDefault") is { } p)
        {
            // Restore size before clamping so the clamp sees the real extents; reject
            // degenerate sizes from a hand-edited file (throwaway state, never trusted).
            if (p.Width is { } w && w >= MinWidth) Width = w;
            if (p.Height is { } h && h >= MinHeight) Height = h;
            (Left, Top) = ScreenClamp.Clamp(p.X, p.Y, Width, Height,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
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

    protected override void OnClosed(EventArgs e)
    {
        // The settings service outlives this per-session window: unsubscribe or every opened-and-
        // closed details window would leak its predecessor through this Changed subscription.
        _settings.Changed -= OnSettingsChanged;
        _stateStore.Save("sessionDetailsDefault", new WindowPlacement(Left, Top, Width, Height));
        _registry.Unregister(_sessionId, Close);
        base.OnClosed(e);
    }
}

using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private bool _closeConfirmed;   // set by SaveThenCloseAsync so the re-entrant Close skips the prompt

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

    /// <summary>Stage 5.4 5.1 close guard: a dirty editor prompts Save / Discard / Cancel
    /// (Yes/No/Cancel), fixing the "title typed then X" data-loss path. WPF cannot await inside
    /// OnClosing, so Save CANCELS this close, awaits the commit, then re-Closes with
    /// _closeConfirmed set. Mirrors MattersPage.OnDeleteMatter's MessageBox confirm pattern.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        // Force-commit a focused LostFocus-bound TextBox (the speaker-count boxes) so IsDirty
        // and the VM working copy reflect what is on screen before we decide anything.
        if (Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!_vm.IsDirty) return;

        var choice = MessageBox.Show(
            "Save changes to this session before closing?\n\nYes saves, No discards the changes, Cancel keeps editing.",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.Cancel) { e.Cancel = true; return; }
        if (choice == MessageBoxResult.No)
        {
            _vm.DiscardCommand.Execute(null);   // revert, then let the close proceed
            return;
        }
        e.Cancel = true;                        // Yes: the commit is async - stop THIS close
        _ = SaveThenCloseAsync();
    }

    /// <summary>Awaits the explicit save, then closes only if it actually settled clean.
    /// Ordering is safe: SaveAsync posts its completion (IsDirty/Saved) via Dispatcher
    /// BeginInvoke BEFORE its task completes, and this await's own dispatcher continuation is
    /// queued after task completion, so IsDirty here reliably reflects the outcome. A failed
    /// save (error already reported) or a declined attribution warning leaves the editor dirty
    /// and the window OPEN. SaveAsync catches all exceptions, so the discard is safe.</summary>
    private async System.Threading.Tasks.Task SaveThenCloseAsync()
    {
        await _vm.SaveCommand.ExecuteAsync(null);
        if (_vm.IsDirty) return;                // save failed or was declined - stay open
        _closeConfirmed = true;
        Close();
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

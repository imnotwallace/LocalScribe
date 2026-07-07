using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
namespace LocalScribe.App;

/// <summary>Formats DisplayRow.StartMs per the settings snapshot the VM loaded with, using
/// the canonical TimestampFormat (same stamps as the file renders). The window assigns Vm
/// before rows render (LoadAsync completes before Rows populate).</summary>
public sealed class ReadViewStampConverter : IValueConverter
{
    public ReadViewViewModel? Vm { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Vm is not null && value is long ms
            ? TimestampFormat.Stamp(ms, Vm.TimestampsMode, Vm.StartedAtLocal)
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>One instance per opened session (design section 2/5). Genuinely closes (nothing
/// depends on it - unlike the live view's hide-on-close). Registered in WindowRegistry so
/// session delete can close read views first and release audio handles. Capture-excluded by
/// default per settings.Privacy (design section 2) via the shared CaptureExclusion.Apply helper
/// (Task 13). Placement: "readViewDefault" written by the LAST closed read view; new windows
/// cascade +24px per already-open read view, screen-clamped.</summary>
public partial class ReadViewWindow
{
    private readonly ReadViewViewModel _vm;
    private readonly string _sessionId;
    private readonly WindowRegistry _registry;
    private readonly WindowStateStore _stateStore;
    private readonly ISettingsService _settings;
    private readonly Action<string> _openSplitSpeakers;
    private readonly Action<string> _openSessionDetails;
    private readonly int _openAtCreation;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _hwndReady;

    public ReadViewWindow(ReadViewViewModel vm, string sessionId, WindowRegistry registry,
        WindowStateStore stateStore, ISettingsService settings, Action<string> openSplitSpeakers,
        Action<string> openSessionDetails)
    {
        InitializeComponent();
        (_vm, _sessionId, _registry, _stateStore, _settings, _openSplitSpeakers, _openSessionDetails)
            = (vm, sessionId, registry, stateStore, settings, openSplitSpeakers, openSessionDetails);
        DataContext = vm;
        ((ReadViewStampConverter)Resources["Stamp"]).Vm = vm;
        _openAtCreation = registry.OpenCount;                        // count BEFORE registering this window
        registry.Register(sessionId, Close);
        // Re-apply capture exclusion when Privacy.ExcludeWindowsFromCapture is toggled while this
        // read view is open (design 2 + 6.2: applies immediately), mirroring Main/LiveViewWindow.
        // This is a per-session window that genuinely closes, so OnClosed MUST unsubscribe.
        _settings.Changed += OnSettingsChanged;
        // IsAvailable is published on a later dispatcher turn inside Apply (via _dispatch =
        // Dispatcher.BeginInvoke), so the post-await read below can race it. Subscribing here
        // makes the timer start the moment IsAvailable flips true, whichever order wins.
        _vm.Playback.PropertyChanged += OnPlaybackPropertyChanged;
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            if (_vm.Playback.IsAvailable && !_tick.IsEnabled) _tick.Start(); // fast path if already published
        };
        _tick.Tick += (_, _) => _vm.TickPlayback();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);

        var saved = _stateStore.Load("readViewDefault");
        var p = ReadViewPlacement.Next(saved, _openAtCreation, Width, Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (p.X, p.Y);
        if (p.Width is double w) Width = w;
        if (p.Height is double h) Height = h;
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

    // Idempotent: the ctor subscription and the post-await fast path in the Loaded handler above
    // both race to start _tick, whichever order Apply publishes IsAvailable in; the IsEnabled guard
    // ensures the timer starts exactly once.
    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.IsAvailable)
            && _vm.Playback.IsAvailable && !_tick.IsEnabled)
            _tick.Start();
    }

    private void OnSplitSpeakers(object sender, RoutedEventArgs e) => _openSplitSpeakers(_sessionId);

    // Click-to-jump (design 4.1 Task 7): double-clicking a transcript section seeks playback to
    // its start and resumes there; the highlight follows via TickPlayback's PlayingSectionIndex.
    private void OnRowActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowList.SelectedIndex >= 0) _vm.JumpToSection(RowList.SelectedIndex);
    }

    // ---- Stage 6.1: row context-menu editing -------------------------------------------------

    /// <summary>Marker rows have nothing to edit: suppress the menu outright rather than show
    /// three disabled items.</summary>
    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReadRow row && !row.HasSegments)
            e.Handled = true;
    }

    private async void OnCorrectText(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReadRow row) return;
        var editor = _vm.CreateCorrectionEditor(_vm.Rows.IndexOf(row));
        if (editor is null) return;
        var dialog = new CorrectTextDialog(editor) { Owner = this };
        if (dialog.ShowDialog() == true) await ReloadPreservingScrollAsync();
    }

    private async void OnReassignSpeaker(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReadRow row) return;
        var editor = _vm.CreateReassignEditor(_vm.Rows.IndexOf(row));
        if (editor is null) return;
        editor.OpenSessionDetailsRequested += _openSessionDetails;
        var dialog = new ReassignSpeakerDialog(editor) { Owner = this };
        if (dialog.ShowDialog() == true) await ReloadPreservingScrollAsync();
    }

    private async void OnRemovePin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ReadRow row) return;
        var confirmed = MessageBox.Show(this,
            "Remove the manual speaker pin(s) on this section? The label falls back to the automatic result; nothing else changes.",
            "Remove speaker pin", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        await _vm.RemovePinsAsync(_vm.Rows.IndexOf(row), CancellationToken.None);
        await ReloadPreservingScrollAsync();
    }

    /// <summary>Rows.Clear() resets the virtualized ListView's scroll position; capture and
    /// restore the vertical offset so an edit does not bounce the reader to the top. Restore is
    /// dispatched (layout must run once over the new rows before the offset is valid).</summary>
    private async Task ReloadPreservingScrollAsync()
    {
        var scroll = FindScrollViewer(RowList);
        double offset = scroll?.VerticalOffset ?? 0;
        await _vm.ReloadRowsAsync(CancellationToken.None);
        if (scroll is not null)
            // Discard: DispatcherOperation is awaitable on this runtime, so a bare call in this
            // async method trips CS4014 (0-warning gate) - the restore is deliberately fire-and-
            // forget (same house style as the RefreshRowAsync fire-and-forgets in App.xaml.cs).
            _ = Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is ScrollViewer nested) return nested;
        }
        return null;
    }

    // Scrubbing guard (design 4.1 Task 4, revised Stage 5.4 smoke-fix): Playback.IsScrubbing
    // suppresses the position timer's Tick() - AND the TwoWay SliderValueMs binding's own commit
    // path - while the user is mid-drag, so neither can fight the thumb; DragCompleted commits the
    // final value via Seek() on release. Track-click and arrow/Page/Home/End keys never raise
    // Thumb.DragStarted/Completed (Slider's class handlers move Value directly), so those gestures
    // commit immediately through PlaybackViewModel.OnSliderValueMsChanged instead - there is
    // nothing left for a Preview*/KeyDown instance handler to do for them.
    private void OnSeekDragStarted(object sender, RoutedEventArgs e)
        => _vm.Playback.IsScrubbing = true;

    private void OnSeekDragCompleted(object sender, RoutedEventArgs e)
    {
        _vm.Playback.Seek(_vm.Playback.SliderValueMs);
        _vm.Playback.IsScrubbing = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        // The settings service outlives this per-session window: unsubscribe or every opened-and-
        // closed read view would leak its predecessor through this Changed subscription.
        _settings.Changed -= OnSettingsChanged;
        _vm.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        _vm.Dispose();                                               // releases both MediaPlayer file handles
        _registry.Unregister(_sessionId, Close);                     // remove ONLY this window's entry -
                                                                      // a Split-speakers dialog for the same
                                                                      // session id may still be open
        if (_registry.OpenCount == 0)                                // last closed read view writes the default
            _stateStore.Save("readViewDefault", new WindowPlacement(Left, Top, Width, Height));
        base.OnClosed(e);
    }
}

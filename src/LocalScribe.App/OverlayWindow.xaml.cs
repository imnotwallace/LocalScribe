using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
namespace LocalScribe.App;

/// <summary>The recording pill (design decision 12): topmost, no-activate (clicking Pause
/// mid-call never steals focus from the meeting), excluded from screen capture by default,
/// draggable with a remembered clamped position. Show/Hide is driven by App via
/// OverlayViewModel.IsVisible - this window never closes itself.</summary>
public partial class OverlayWindow
{
    private static readonly Brush RecordingBrush =
        new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23));
    private static readonly Brush PausedBrush =
        new SolidColorBrush(Color.FromRgb(0xF7, 0x63, 0x0C));

    private readonly OverlayViewModel _vm;
    private readonly WindowStateStore _stateStore;

    public OverlayWindow(OverlayViewModel vm, WindowStateStore stateStore)
    {
        InitializeComponent();
        (_vm, _stateStore) = (vm, stateStore);
        DataContext = vm;
        vm.Session.PropertyChanged += OnSessionChanged;
        UpdateStateVisuals();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowInterop.MakeNoActivate(this);
        if (_vm.ExcludeFromCapture) NativeWindowInterop.ExcludeFromCapture(this);

        var pos = _stateStore.Load();
        var (x, y) = ScreenClamp.Clamp(pos?.X ?? double.NaN, pos?.Y ?? double.NaN,
            Width, ActualHeight > 0 ? ActualHeight : 56,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (x, y);
    }

    private void OnDragMove(object sender, MouseButtonEventArgs e)
    {
        DragMove();
        _stateStore.Save(Left, Top);
    }

    // Fire-and-forget into the shared commands; Focusable=False keeps focus in the meeting.
    private void OnPauseResume(object sender, RoutedEventArgs e)
        => _vm.Session.PauseResumeCommand.Execute(null);

    private void OnStop(object sender, RoutedEventArgs e)
        => _vm.Session.StopCommand.Execute(null);

    private void OnSessionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.State)) UpdateStateVisuals();
    }

    private void UpdateStateVisuals()
    {
        bool paused = _vm.Session.State == SessionState.Paused;
        StateDot.Fill = paused ? PausedBrush : RecordingBrush;
        PauseButton.Content = paused ? "\uE768" : "\uE769";   // Segoe Fluent: play / pause
    }
}

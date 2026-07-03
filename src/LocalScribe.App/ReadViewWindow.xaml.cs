using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
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
    private readonly int _openAtCreation;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _seekDragging;

    public ReadViewWindow(ReadViewViewModel vm, string sessionId, WindowRegistry registry,
        WindowStateStore stateStore, ISettingsService settings)
    {
        InitializeComponent();
        (_vm, _sessionId, _registry, _stateStore, _settings) = (vm, sessionId, registry, stateStore, settings);
        DataContext = vm;
        ((ReadViewStampConverter)Resources["Stamp"]).Vm = vm;
        _openAtCreation = registry.OpenCount;                        // count BEFORE registering this window
        registry.Register(sessionId, Close);
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            if (_vm.Playback.IsAvailable) _tick.Start();             // same ~150 ms pattern as the live view timer
        };
        _tick.Tick += (_, _) =>
        {
            _vm.Playback.Tick();
            SeekSlider.Maximum = Math.Max(1, _vm.Playback.DurationMs);
            if (!_seekDragging) SeekSlider.Value = _vm.Playback.PositionMs;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);

        var saved = _stateStore.Load("readViewDefault");
        var p = ReadViewPlacement.Next(saved, _openAtCreation, Width, Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (p.X, p.Y);
        if (p.Width is double w) Width = w;
        if (p.Height is double h) Height = h;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        _vm.Playback.PlayPauseCommand.Execute(null);
        PlayPauseButton.Content = _vm.Playback.IsPlaying ? "Pause" : "Play";
    }

    private void OnSeekDragStarted(object sender, RoutedEventArgs e) => _seekDragging = true;

    private void OnSeekDragCompleted(object sender, RoutedEventArgs e)
    {
        _seekDragging = false;
        _vm.Playback.Seek((long)SeekSlider.Value);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        _vm.Dispose();                                               // releases both MediaPlayer file handles
        _registry.Unregister(_sessionId);
        if (_registry.OpenCount == 0)                                // last closed read view writes the default
            _stateStore.Save("readViewDefault", new WindowPlacement(Left, Top, Width, Height));
        base.OnClosed(e);
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
namespace LocalScribe.App;

/// <summary>Thin shell over the shared VMs. Bottom-sticky auto-scroll: follows new lines only
/// while the user is at the bottom. Closing HIDES - a recording must never die with a window;
/// only tray Exit shuts the app down. Capture-excluded per settings.Privacy (design section 2);
/// this is a hide-on-close singleton that lives for the app lifetime, so the Changed
/// subscription is intentionally never removed.</summary>
public partial class LiveViewWindow
{
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines, RecordingConsoleViewModel Console);

    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    private bool _stickToBottom = true;
    private bool _hwndReady;

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, ISettingsService settings)
    {
        InitializeComponent();
        (_lines, _settings) = (lines, settings);
        DataContext = new LiveViewContext(session, lines, console);
        lines.Lines.CollectionChanged += OnLinesChanged;
        settings.Changed += OnSettingsChanged;
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

    private void OnLinesChanged(object? _, NotifyCollectionChangedEventArgs e)
    {
        if (_stickToBottom && _lines.Lines.Count > 0)
            LineList.ScrollIntoView(_lines.Lines[^1]);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (FindScrollViewer(LineList) is { } sv)
            sv.ScrollChanged += (_, args) =>
                _stickToBottom = args.VerticalOffset >= args.ExtentHeight - args.ViewportHeight - 2;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;                       // hide, never close
        Hide();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } deep) return deep;
        }
        return null;
    }
}

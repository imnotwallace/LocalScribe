using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Views;

/// <summary>One toast button: Caption is the visible label; OnClick runs on the UI thread when
/// clicked, and the toast closes itself afterwards regardless.</summary>
public sealed record ToastAction(string Caption, Action OnClick);

/// <summary>The advisory toast primitive (design 2026-07-18 section 4; REUSED by section 5's
/// call-detect offer toast - the ctor + ToastAction shape is a LOCKED cross-branch contract).
/// A PLAIN WPF Window - never FluentWindow/Mica (pre-pump invisible-Mica gotcha, project memory) -
/// frameless, Topmost, and NO-ACTIVATE (ShowActivated/Focusable false in XAML plus
/// WS_EX_NOACTIVATE via NativeWindowInterop.MakeNoActivate, the OverlayWindow pattern), so it can
/// never steal focus from a live call. Bottom-right of the primary work area via the pure, tested
/// ToastPlacement helper; auto-dismisses after autoDismissSeconds (&lt;= 0 = sticky). Callers show
/// it only after the message pump is up (dispatcher-marshalled paths only). ADVISORY ONLY: a toast
/// never writes markers and never gates recording - actions route through the same shared commands
/// the user would click (locked rule, design section 1).</summary>
public partial class AdvisoryToastWindow : Window
{
    private readonly DispatcherTimer? _dismiss;

    public AdvisoryToastWindow(string title, string body, IReadOnlyList<ToastAction> actions,
        int autoDismissSeconds)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        foreach (var action in actions)
        {
            var button = new Button
            { Content = action.Caption, Focusable = false, Margin = new Thickness(8, 0, 0, 0) };
            button.Click += (_, _) => { try { action.OnClick(); } finally { Close(); } };
            ActionsPanel.Children.Add(button);
        }
        if (ToastPlacement.DismissInterval(autoDismissSeconds) is { } interval)
        {
            _dismiss = new DispatcherTimer { Interval = interval };
            _dismiss.Tick += (_, _) => Close();
            _dismiss.Start();
        }
        // Position on Loaded: with SizeToContent="Height", ActualHeight is only real after the
        // first layout pass. SystemParameters.WorkArea = the PRIMARY work area (design 5.3).
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            (Left, Top) = ToastPlacement.BottomRight(wa.Left, wa.Top, wa.Width, wa.Height,
                ActualWidth, ActualHeight);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeWindowInterop.MakeNoActivate(this);   // WS_EX_NOACTIVATE + TOOLWINDOW (existing helper)
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismiss?.Stop();
        base.OnClosed(e);
    }
}

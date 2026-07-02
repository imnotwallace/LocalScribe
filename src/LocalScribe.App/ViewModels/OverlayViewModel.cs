using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>Overlay pill state (spec 2.1): visible only in Recording/Paused, supplements -
/// never replaces - the tray consent indicator. Session name is opt-in tooltip-only (design
/// decision 12: privileged matter must not render on an always-on-top surface by default).</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly Settings _settings;

    public SessionViewModel Session { get; }
    public bool ShowLevelMeter => _settings.Overlay.ShowLevelMeter;
    public bool IsVisible => _settings.Overlay.Enabled
        && Session.State is SessionState.Recording or SessionState.Paused;
    public string? TooltipText => _settings.Overlay.ShowSessionName ? Session.CurrentSessionId : null;

    // Settings-mirroring pass-through the window reads from OnSourceInitialized (design
    // decision 12: capture exclusion is default-on but must stay a real, inspectable setting).
    public bool ExcludeFromCapture => _settings.Overlay.ExcludeFromCapture;

    public OverlayViewModel(SessionViewModel session, Settings settings)
    {
        (Session, _settings) = (session, settings);
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.State))
            {
                OnPropertyChanged(nameof(IsVisible));
                OnPropertyChanged(nameof(TooltipText));
            }
        };
    }
}

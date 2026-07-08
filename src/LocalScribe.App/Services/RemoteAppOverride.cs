using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session target-app override (design 5.4 section 6, locked
/// decision 3 of the resolved-decisions list; widened by the device-selection design section 5):
/// seeds from Settings.Remote.App, changes the target for THIS recording only, and never writes
/// back to settings.json - Settings stays the single persistent source of truth. CompositionRoot
/// composes Apply over the one live Func&lt;Settings&gt; that SessionController and
/// WasapiCaptureSourceProvider resolve at Start/Resume, so the override reaches capture planning
/// with zero Core changes. When set, forces Remote to PerProcess on the override app regardless
/// of the base mode - so picking an app in Auto captures exactly that app for the session; unset
/// is identity, leaving Auto's auto-detect or SystemMix's shared image untouched. The console
/// only ever sets an override in a mode where the app selector is shown (Auto/PerProcess, never
/// SystemMix), so this never falsifies a SystemMix session's RemoteSnapshot.App. WPF-free,
/// trivially small; written from the UI thread (console selector) and read at capture-plan
/// time.</summary>
public sealed class RemoteAppOverride
{
    private volatile string? _app;

    /// <summary>The override image name; null or empty means "use the saved Settings value".</summary>
    public string? App { get => _app; set => _app = value; }

    /// <summary>Returns settings with Remote forced to PerProcess on the override app when one is
    /// set (design section 5: an explicitly chosen app is captured per-app for the session,
    /// regardless of the base mode), otherwise the input unchanged. Pure with respect to the input.
    /// The console only ever sets an override in a mode where the app selector is shown
    /// (Auto/PerProcess, never SystemMix), so this never falsifies a SystemMix session.</summary>
    public Settings Apply(Settings s)
        => _app is { Length: > 0 } app
            ? s with { Remote = s.Remote with { Mode = RemoteMode.PerProcess, App = app } }
            : s;
}

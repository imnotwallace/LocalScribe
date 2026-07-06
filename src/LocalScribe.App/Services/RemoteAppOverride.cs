using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session target-app override (design 5.4 section 6, locked
/// decision 3 of the resolved-decisions list): seeds from Settings.Remote.App, changes the
/// target for THIS recording only, and never writes back to settings.json - Settings stays the
/// single persistent source of truth. CompositionRoot composes Apply over the one live
/// Func&lt;Settings&gt; that SessionController and WasapiCaptureSourceProvider resolve at
/// Start/Resume, so the override reaches capture planning with zero Core changes. Applies ONLY
/// in PerProcess mode: in Auto the planner ignores Remote.App, and in SystemMix overriding it
/// would falsify the RemoteSnapshot.App recorded into session.json. WPF-free, trivially small;
/// written from the UI thread (console selector) and read at capture-plan time.</summary>
public sealed class RemoteAppOverride
{
    private volatile string? _app;

    /// <summary>The override image name; null or empty means "use the saved Settings value".</summary>
    public string? App { get => _app; set => _app = value; }

    /// <summary>Returns settings with Remote.App replaced by the override when it applies,
    /// otherwise the input unchanged. Pure with respect to the input (records are immutable).</summary>
    public Settings Apply(Settings s)
        => _app is { Length: > 0 } app && s.Remote.Mode == RemoteMode.PerProcess
            ? s with { Remote = s.Remote with { App = app } }
            : s;
}

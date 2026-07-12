using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session Remote-target override (design 2026-07-12), the exact
/// twin of MicOverride (which holds a full MicSetting). Widened from the old app-string-only
/// RemoteAppOverride to carry a full RemoteSetting (Mode + App), so Auto / a specific app / system
/// mix are all expressible for THIS recording only. Seeds from Settings.Remote when the console
/// opens, is set as the user picks (and by a live switch), clears on Idle, and NEVER writes back to
/// settings.json - Settings stays the single persistent source of truth. CompositionRoot composes
/// Apply over the one live Func&lt;Settings&gt; that SessionController and WasapiCaptureSourceProvider
/// resolve at Start/Resume, so the override reaches capture planning with zero Core changes. WPF-free;
/// written from the UI thread (picker) and read at capture-plan time, hence the volatile field.</summary>
public sealed class RemoteTargetOverride
{
    private volatile RemoteSetting? _override;

    /// <summary>The session's chosen remote target, or null to let the persistent Settings.Remote
    /// stand. Written from the UI thread; read at capture-plan time (Start/Resume) - mirrors
    /// MicOverride's cross-thread pattern.</summary>
    public RemoteSetting? Override { get => _override; set => _override = value; }

    /// <summary>Returns settings with Remote replaced by the override when set, otherwise the input
    /// unchanged. Pure with respect to the input (records are immutable).</summary>
    public Settings Apply(Settings s) => _override is { } r ? s with { Remote = r } : s;
}

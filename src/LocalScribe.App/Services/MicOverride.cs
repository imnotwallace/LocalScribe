using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session microphone override (design section 3), twin of
/// RemoteAppOverride. Set by the console's mic picker; cleared on Idle (session end). Holds a full
/// MicSetting (not just a device id) so a session can override the persistent pin BACK to
/// follow-default too. CompositionRoot composes Apply over the one live Func&lt;Settings&gt; that
/// SessionController and WasapiCaptureSourceProvider resolve at Start/Resume, so the override
/// reaches capture with zero Core changes and is NEVER persisted to settings.json. WPF-free.</summary>
public sealed class MicOverride
{
    /// <summary>The session's chosen mic, or null to let the persistent Settings pin (or
    /// follow-default) stand.</summary>
    public MicSetting? Override { get; set; }

    /// <summary>Returns settings with Mic replaced by the override when set, otherwise the input
    /// unchanged. Pure with respect to the input (records are immutable).</summary>
    public Settings Apply(Settings s) => Override is { } m ? s with { Mic = m } : s;
}

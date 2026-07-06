using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>An active render audio session: pid + extensionless image name.</summary>
public sealed record AudioSessionInfo(uint Pid, string ProcessName);

/// <summary>The resolved remote capture decision (spec 12.1). Mode is never Auto here.</summary>
public sealed record RemotePlan(RemoteMode Mode, uint? Pid, string? App, bool FellBackToSystemMix, string? Notice);

/// <summary>Pure spec-12.1 remote resolution: per-process for clean apps (Webex/Zoom), ALWAYS
/// system-mix for the known all-zeros/shared-audio set (Teams, browsers) - even when the user
/// explicitly asked for perProcess - and system-mix fallback when nothing matches. A legal
/// recording must never silently produce an empty remote stream.</summary>
public static class RemoteCapturePlanner
{
    /// <summary>Known-good per-process capture targets offered as picker suggestions (Stage 5.4
    /// section 6). Deliberately ONLY the clean per-process apps: everything in FullMix is captured
    /// as system mix regardless, so suggesting it would be dishonest. Free text stays allowed -
    /// any render-session image name is a legal target.</summary>
    public static IReadOnlyList<string> SuggestedPerProcessApps { get; } = ["CiscoCollabHost", "Webex", "Zoom"];

    // Priority order for Auto (Stage-1 finding: Webex renders call audio in CiscoCollabHost.exe).
    private static readonly string[] Priority =
        ["CiscoCollabHost", "Webex", "Zoom", "ms-teams", "msedgewebview2", "Teams"];

    // Known all-zeros (Teams registers two render sessions on one PID) or shared-audio-process
    // (browsers/webviews) images: per-process loopback is silent or bleeds - use system mix.
    private static readonly string[] FullMix =
        ["ms-teams", "Teams", "msedgewebview2", "chrome", "msedge", "firefox", "brave", "opera"];

    public static RemotePlan Plan(IReadOnlyList<AudioSessionInfo> active, RemoteSetting setting)
    {
        if (setting.Mode == RemoteMode.SystemMix)
            return new RemotePlan(RemoteMode.SystemMix, null, setting.App, FellBackToSystemMix: false, Notice: null);

        if (setting.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(setting.App))
        {
            var match = FirstMatch(active, [setting.App]);
            if (match is null)
                return Fallback(setting.App,
                    $"requested app '{setting.App}' has no active render session; capturing full system mix");
            if (IsFullMix(match.ProcessName) || IsFullMix(setting.App))
                return Fallback(match.ProcessName,
                    $"'{match.ProcessName}' cannot be captured per-process (all-zeros/shared audio); capturing full system mix");
            return new RemotePlan(RemoteMode.PerProcess, match.Pid, match.ProcessName, false, null);
        }

        // Auto: scan by priority.
        var found = FirstMatch(active, Priority);
        if (found is null)
            return Fallback(null, "no meeting app render session found; capturing full system mix");
        if (IsFullMix(found.ProcessName))
            return Fallback(found.ProcessName,
                $"'{found.ProcessName}' cannot be captured per-process (all-zeros/shared audio); capturing full system mix");
        return new RemotePlan(RemoteMode.PerProcess, found.Pid, found.ProcessName, false, null);
    }

    private static RemotePlan Fallback(string? app, string notice)
        => new(RemoteMode.SystemMix, null, app, FellBackToSystemMix: true, Notice: notice);

    private static bool IsFullMix(string image)
        => FullMix.Any(n => image.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static AudioSessionInfo? FirstMatch(IReadOnlyList<AudioSessionInfo> active, string[] names)
    {
        foreach (var name in names)
            foreach (var s in active)
                if (s.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }
}

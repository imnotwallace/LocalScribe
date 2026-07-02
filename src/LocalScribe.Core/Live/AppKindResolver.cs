using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>Maps a planner-resolved process image to the AppKind recorded in session.json
/// (design 7.4 - the Stage 3b deferral). Same containment matching as RemoteCapturePlanner:
/// extensionless image names, case-insensitive. Null/unknown resolves to Manual so the caller
/// never has to special-case an unresolved plan. NOTE: "msedgewebview2" is checked in the
/// Browser bucket (locked mapping) - a Teams webview render session is a Browser capture
/// characteristic-wise, and the dedicated "ms-teams" image is what identifies real Teams.</summary>
public static class AppKindResolver
{
    public static AppKind FromProcessImage(string? processImage)
    {
        if (string.IsNullOrWhiteSpace(processImage)) return AppKind.Manual;
        if (Has(processImage, "CiscoCollabHost") || Has(processImage, "Webex")) return AppKind.Webex;
        if (Has(processImage, "Zoom")) return AppKind.Zoom;
        if (Has(processImage, "msedgewebview2") || Has(processImage, "chrome")
            || Has(processImage, "msedge") || Has(processImage, "firefox")
            || Has(processImage, "brave") || Has(processImage, "opera")) return AppKind.Browser;
        if (Has(processImage, "ms-teams") || Has(processImage, "Teams")) return AppKind.Teams;
        return AppKind.Manual;
    }

    private static bool Has(string image, string name)
        => image.Contains(name, StringComparison.OrdinalIgnoreCase);
}

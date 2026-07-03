using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>Pure decision half of transcript-window capture exclusion (the one-line interop
/// half is src/LocalScribe.App/CaptureExclusion.cs): re-apply display affinity only when the
/// Privacy toggle actually changed, so unrelated settings saves never touch the HWND.</summary>
public static class CaptureExclusionPolicy
{
    public static bool ShouldReapply(Settings oldSettings, Settings newSettings)
        => oldSettings.Privacy.ExcludeWindowsFromCapture
           != newSettings.Privacy.ExcludeWindowsFromCapture;
}

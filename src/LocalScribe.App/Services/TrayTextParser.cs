namespace LocalScribe.App.Services;

/// <summary>Parses the Windows 11 call-mute tray icon's UIA Name into an AppMuteReading (design
/// 2026-07-11 section 2). Reads only the first line of the flyout text - the body below it (hotkey
/// hint, "Apps using your microphone" list) is not load-bearing. Never throws; anything that does
/// not match one of the two captured prefixes fails open to Unknown.</summary>
public static class TrayTextParser
{
    // captured 2026-07-11, runbook rev 2026-07-11 (uia-dump-20260711-0916xx)
    private const string MutedPrefix = "Microphone Muted: ";
    // captured 2026-07-11, runbook rev 2026-07-11 (uia-dump-20260711-0916xx)
    private const string UnmutedPrefix = "Microphone Unmuted: ";

    public static AppMuteReading Parse(string? trayIconName)
    {
        if (string.IsNullOrEmpty(trayIconName))
            return new(AppMuteState.Unknown, null);

        var firstLine = trayIconName.Split('\n')[0].TrimEnd('\r').Trim();
        if (firstLine.Length == 0)
            return new(AppMuteState.Unknown, null);

        // firstLine is already whole-line .Trim()'d above, so a match on the space-terminated
        // prefix guarantees at least one non-space char follows it - an empty app name cannot reach
        // here (the old app.Length==0 -> Unknown fallback was dead code). The remaining .Trim() only
        // normalises the harmless case of extra spaces between the prefix and the app name.
        if (firstLine.StartsWith(MutedPrefix, System.StringComparison.Ordinal))
            return new(AppMuteState.Muted, firstLine[MutedPrefix.Length..].Trim());

        if (firstLine.StartsWith(UnmutedPrefix, System.StringComparison.Ordinal))
            return new(AppMuteState.Live, firstLine[UnmutedPrefix.Length..].Trim());

        return new(AppMuteState.Unknown, null);
    }
}

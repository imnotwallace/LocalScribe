using Microsoft.Win32;

namespace LocalScribe.App.Services;

/// <summary>Per-user localscribe:// scheme registration (design 2026-07-18 section 4) - the
/// unpackaged-app pattern: HKCU\Software\Classes\localscribe with the empty "URL Protocol" value
/// plus shell\open\command = "&lt;exe&gt;" "%1". Humble Object (RegistryLaunchAtLogin precedent):
/// registry access is untestable headless, so the write path stays one-line-per-value and the
/// smoke runbook verifies it; RegistrationValues is the pure, tested composition. Idempotent
/// (identical overwrites every launch), NEVER elevates (HKCU only), best-effort - any failure is
/// swallowed and deep links simply stay dark; startup is never blocked.</summary>
public static class DeepLinkRegistrar
{
    public const string SchemeKeyPath = @"Software\Classes\localscribe";

    /// <summary>Pure value composition: the exe path AND the %1 URL placeholder are BOTH quoted
    /// so paths with spaces and argument splitting can never mangle the forwarded URL.</summary>
    public static (string ProtocolLabel, string OpenCommand) RegistrationValues(string exePath)
        => ("URL:LocalScribe deep link", "\"" + exePath + "\" \"%1\"");

    public static void EnsureRegistered(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        try
        {
            var (label, command) = RegistrationValues(exePath);
            using var key = Registry.CurrentUser.CreateSubKey(SchemeKeyPath);
            key.SetValue(null, label);
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, command);
        }
        catch { /* best-effort per the class contract */ }
    }
}

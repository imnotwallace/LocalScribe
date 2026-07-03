using Microsoft.Win32;

namespace LocalScribe.App.Services;

/// <summary>HKCU Run-key launch-at-login (design 6.1: launchAtLogin WIRED in Stage 4).
/// Humble Object: registry access is untestable headless, so this stays one-line-per-branch
/// and the smoke runbook (C9) verifies it; SettingsPageViewModel is tested against a fake.</summary>
public sealed class RegistryLaunchAtLogin : ILaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LocalScribe";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public void SetEnabled(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (on) key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

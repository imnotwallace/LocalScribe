namespace LocalScribe.App.Services;

/// <summary>Launch-at-login seam (design 6.1, App group). The registry implementation is a
/// Humble Object (RegistryLaunchAtLogin) verified by the smoke runbook, not unit tests;
/// SettingsPageViewModel is tested against a fake.</summary>
public interface ILaunchAtLogin
{
    bool IsEnabled();
    void SetEnabled(bool on);
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;

namespace LocalScribe.App.ViewModels;

/// <summary>First-run consent notice VM (design 6.3). WPF-free. The wording reuses the README
/// "Privacy" draft language. Accept persists consentNotice { acknowledgedAtUtc, appVersion } as
/// an additive settings field, then raises Closed(true); Decline raises Closed(false) and
/// persists nothing - the App layer shuts down. Detection is field-absence
/// (settings.Current.ConsentNotice is null), never file-absence; Record is never re-gated
/// after acceptance (manual-only start remains the consent posture).</summary>
public sealed partial class ConsentViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly string _appVersion;

    /// <summary>Raised exactly once: true after the acknowledgment is persisted, false on
    /// decline. The dialog closes on it; the App shuts down on false.</summary>
    public event Action<bool>? Closed;

    public ConsentViewModel(ISettingsService settings, TimeProvider time, string appVersion)
        => (_settings, _time, _appVersion) = (settings, time, appVersion);

    public string Title { get; } = "Before you record";

    public string SummaryText { get; } =
        "LocalScribe records your microphone and the meeting's audio, transcribes them with a "
        + "locally-run model, and stores everything on this machine. Audio and transcripts "
        + "never leave your computer; nothing is uploaded anywhere. A visible tray indicator "
        + "(and an on-screen overlay) shows whenever recording is active.";

    public string ResponsibilityText { get; } =
        "Recording others is your responsibility. Many jurisdictions require the consent of "
        + "some or all parties before a conversation may be recorded (two-party / all-party "
        + "consent). LocalScribe makes the recording state obvious but cannot enforce the law "
        + "or obtain consent for you - disclosing the recording to the other participants is "
        + "up to you.";

    [RelayCommand]
    private async Task AcceptAsync()
    {
        await _settings.SaveAsync(_settings.Current with
        {
            ConsentNotice = new ConsentSetting
            { AcknowledgedAtUtc = _time.GetUtcNow(), AppVersion = _appVersion },
        }, CancellationToken.None);
        Closed?.Invoke(true);
    }

    [RelayCommand]
    private void Decline() => Closed?.Invoke(false);
}

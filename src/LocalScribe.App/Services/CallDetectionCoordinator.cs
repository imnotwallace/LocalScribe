using LocalScribe.Core.Live;
using LocalScribe.Core.Model;

namespace LocalScribe.App.Services;

/// <summary>Glue between the Core watcher/policy/advisor and the App's advisory toasts (design
/// 2026-07-18 sections 5.2-5.4). Assembles CallDetectionSnapshots from live seams, owns the
/// per-exe offer ledger (60 s cooldown, recorded only on actual offers), and arms/disarms the
/// call-end advisor across the recording lifecycle. ADVISORY-ONLY by locked rule and by
/// CONSTRUCTION: it raises OfferRequested/CallEndAdvised and nothing else - it holds no
/// controller, session VM, or command reference, so it structurally cannot start, stop, pause,
/// gate, or mark anything. WPF-free; single-threaded by contract (every call arrives on the UI
/// thread from the 1.5 s poll timer and the State subscription in App.xaml.cs).</summary>
public sealed class CallDetectionCoordinator
{
    private readonly Func<CallDetectSetting> _setting;
    private readonly Func<bool> _recordingActive;
    private readonly Func<bool> _consoleArmed;
    private readonly int _ownPid;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastOfferedAt = new(StringComparer.Ordinal);
    private readonly CallEndAdvisor _endAdvisor = new();

    public CallDetectionCoordinator(Func<CallDetectSetting> setting, Func<bool> recordingActive,
        Func<bool> consoleArmed, int ownPid, TimeProvider time)
        => (_setting, _recordingActive, _consoleArmed, _ownPid, _time)
            = (setting, recordingActive, consoleArmed, ownPid, time);

    /// <summary>An offer decision for the given exe image (already policy-approved and
    /// ledger-recorded). The subscriber shows the offer toast; ignoring it does nothing, ever.</summary>
    public event Action<string>? OfferRequested;

    /// <summary>The one-shot call-end advisory decision (3 s quiet window elapsed while a
    /// recording session is active). The subscriber shows the stop?-toast; recording continues
    /// until a human clicks Stop.</summary>
    public event Action? CallEndAdvised;

    public void OnActivity(CallAppActivity activity)
    {
        _endAdvisor.Observe(activity);
        var s = _setting();
        var decision = CallDetectionPolicy.Decide(activity, new CallDetectionSnapshot(
            s.Enabled, s.Apps, _ownPid, _recordingActive(), _consoleArmed(),
            _lastOfferedAt, _time.GetUtcNow()));
        if (!decision.Offer) return;
        _lastOfferedAt[CallDetectionPolicy.ExeKey(activity.Exe)] = _time.GetUtcNow();
        OfferRequested?.Invoke(activity.Exe);
    }

    /// <summary>Debounce-expiry check, every poll tick. Gated on recordingActive so a stopped
    /// recording can never trail a late end-advisory toast.</summary>
    public void OnTick()
    {
        if (_recordingActive() && _endAdvisor.ShouldAdvise(_time.GetUtcNow()))
            CallEndAdvised?.Invoke();
    }

    /// <summary>Idle-&gt;Recording: arm the advisor with the applied per-process target (else the
    /// allowlisted capture apps live right now - the watcher's ActiveExes snapshot).</summary>
    public void OnRecordingStarted(string? perProcessApp, IReadOnlyCollection<string> activeCaptureExes)
        => _endAdvisor.Arm(perProcessApp, activeCaptureExes, _setting().Apps);

    public void OnRecordingStopped() => _endAdvisor.Disarm();
}

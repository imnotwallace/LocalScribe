namespace LocalScribe.Core.Live;

/// <summary>Everything the offer decision needs, captured by the caller at decision time (design
/// 2026-07-18 section 5.2): master toggle, exe allowlist (Settings spelling, ".exe" tolerated),
/// own PID (LocalScribe's mic capture must never self-offer), recording-active + console-armed
/// suppression inputs, the per-exe last-offered ledger (ExeKey-keyed), and the clock.</summary>
public sealed record CallDetectionSnapshot(
    bool Enabled,
    IReadOnlyList<string> Allowlist,
    int OwnPid,
    bool RecordingActive,
    bool ConsoleArmed,
    IReadOnlyDictionary<string, DateTimeOffset> LastOfferedAt,
    DateTimeOffset Now);

/// <summary>Offer or Ignore(reason). The reason is diagnostics-only text - it is never rendered
/// to the user and never logged with any transcript content.</summary>
public sealed record CallDetectionDecision(bool Offer, string? IgnoreReason)
{
    public static readonly CallDetectionDecision Offered = new(true, null);
    public static CallDetectionDecision Ignore(string reason) => new(false, reason);
}

/// <summary>PURE offer policy for the call-detection advisory (design 2026-07-18 section 5.2).
/// ADVISORY-ONLY by locked rule: this returns a value and touches nothing - starting a recording
/// stays a human click on the offer toast, which runs the same manual-start command path as any
/// other Start. Holds no state; the caller owns the cooldown ledger.</summary>
public static class CallDetectionPolicy
{
    /// <summary>Per-exe re-offer cooldown (Steno's MIC_NOTIFICATION_DEBOUNCE_MS).</summary>
    public static readonly TimeSpan OfferCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Canonical allowlist/ledger key: trim, strip ONE trailing ".exe" (any case), then
    /// lower-invariant. WASAPI session images arrive EXTENSIONLESS (Process.ProcessName) while the
    /// Settings defaults keep the design's exe-name spelling - both forms must meet, both ways.
    /// Shared by the policy, the coordinator's ledger, and CallEndAdvisor's watched-set so the
    /// three can never disagree on identity.</summary>
    public static string ExeKey(string exe)
    {
        string k = exe.Trim();
        if (k.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) k = k[..^4];
        return k.ToLowerInvariant();
    }

    public static CallDetectionDecision Decide(CallAppActivity activity, CallDetectionSnapshot snap)
    {
        if (activity.Kind != CallAppActivityKind.Started)
            return CallDetectionDecision.Ignore("not a session start");
        if (!snap.Enabled)
            return CallDetectionDecision.Ignore("call detection is off");
        if (activity.Pid == snap.OwnPid)
            return CallDetectionDecision.Ignore("own process");
        string key = ExeKey(activity.Exe);
        if (!snap.Allowlist.Any(a => ExeKey(a) == key))
            return CallDetectionDecision.Ignore("not in the allowlist");
        if (snap.RecordingActive)
            return CallDetectionDecision.Ignore("a recording session is active");
        if (snap.ConsoleArmed)
            return CallDetectionDecision.Ignore("the Record console is already open");
        if (snap.LastOfferedAt.TryGetValue(key, out var last) && snap.Now - last < OfferCooldown)
            return CallDetectionDecision.Ignore("per-app cooldown");
        return CallDetectionDecision.Offered;
    }
}

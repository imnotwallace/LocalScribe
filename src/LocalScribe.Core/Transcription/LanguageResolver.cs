namespace LocalScribe.Core.Transcription;

/// <summary>Probe-then-commit per-session language lock (spec section 3): transcribe the first
/// few utterances with detection on, take the majority, lock for the session. A fixed
/// (non-auto) setting locks immediately. Mid-meeting switching is a v1 non-goal.</summary>
public sealed class LanguageResolver
{
    private readonly int _probeCount;
    private readonly List<string> _seen = new();

    public LanguageResolver(string settingsLanguage, int probeCount = 3)
    {
        _probeCount = probeCount;
        if (settingsLanguage != "auto") Locked = settingsLanguage;
    }

    public string? Locked { get; private set; }
    public bool IsLocked => Locked is not null;
    public bool UseEnglishOnlyModel => Locked == "en";

    public void Observe(string? detectedLanguage)
    {
        if (IsLocked || string.IsNullOrEmpty(detectedLanguage)) return;
        _seen.Add(detectedLanguage);
        if (_seen.Count < _probeCount) return;

        Locked = _seen.GroupBy(l => l)
                      .OrderByDescending(g => g.Count())
                      .ThenByDescending(g => _seen.LastIndexOf(g.Key))
                      .First().Key;
    }
}

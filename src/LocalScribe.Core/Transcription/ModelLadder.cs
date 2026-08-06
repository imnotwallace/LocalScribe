namespace LocalScribe.Core.Transcription;

/// <summary>One-step model downgrade for VRAM-OOM / sustained-RTF pressure (spec section 3
/// auto-downgrade triggers). Preserves the .en suffix; null at the floor or for unknown names.</summary>
public static class ModelLadder
{
    // Best -> worst. large-v3-turbo leads (Tier 1 T1-6, spec 2026-08-05 :78-81): it is Rank 0 in
    // WhisperModelCatalog (more accurate per second than large-v3) and it is the IMPORT default, so
    // it is the model an explicit picker most often chooses - and it was the one name for which
    // Downgrade returned null, leaving that user with no VRAM-OOM ladder at all. REJECTED: adding
    // it to BackendSelector's own 3-rung Ladder, which would raise the LIVE ceiling and break the
    // owner's 2026-08-05 ruling that the realtime-factor cap stays. These two arrays are unrelated.
    private static readonly string[] Rungs = { "large-v3-turbo", "large-v3", "medium", "small", "base", "tiny" };

    public static string? Downgrade(string modelName)
    {
        bool en = modelName.EndsWith(".en", StringComparison.Ordinal);
        string stem = en ? modelName[..^3] : modelName;
        int i = Array.IndexOf(Rungs, stem);
        if (i < 0 || i == Rungs.Length - 1) return null;
        string next = Rungs[i + 1];
        return en ? next + ".en" : next;
    }

    /// <summary>True if stem (no ".en" suffix) is one of the known ladder rungs.</summary>
    public static bool IsKnownStem(string stem) => Array.IndexOf(Rungs, stem) >= 0;

    /// <summary>True if the stem (no ".en" suffix) has English-only weights available. Only
    /// tiny/base/small/medium ship ".en" variants - large-v3 has none (there is no
    /// ggml-large-v3.en.bin), so the language-lock weight fix-up must not append ".en" for it
    /// (finding I2: doing so faults engine recreate on a nonexistent model file).</summary>
    public static bool HasEnglishVariant(string stem) => stem is "tiny" or "base" or "small" or "medium";
}

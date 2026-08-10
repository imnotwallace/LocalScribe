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

    /// <summary>The next INSTALLED rung below <paramref name="modelName"/>, or null when none is
    /// on disk. Null is a valid, working answer: the worker reads it as "at the floor" and falls
    /// to CPU on the current weights (TranscriptionWorker.DowngradeAsync).
    ///
    /// There is deliberately NO disk-blind overload. Until 2026-08-11 this stepped by name alone,
    /// and on a machine holding only ggml-large-v3-turbo.bin it returned "large-v3" - which the
    /// factory could not load, throwing out of the worker and deleting a near-complete import.
    /// BackendSelector had consulted ModelPaths.AvailableModels since design section 1; the ladder
    /// simply never did.
    ///
    /// <paramref name="isAvailable"/> takes a canonical model NAME (e.g. "medium.en"), not a file
    /// name - callers resolve quantized variants via ModelFileResolver.IsAvailable.</summary>
    public static string? Downgrade(string modelName, Func<string, bool> isAvailable)
    {
        bool en = modelName.EndsWith(".en", StringComparison.Ordinal);
        string stem = en ? modelName[..^3] : modelName;
        int i = Array.IndexOf(Rungs, stem);
        if (i < 0) return null;
        for (int next = i + 1; next < Rungs.Length; next++)
        {
            string candidate = en ? Rungs[next] + ".en" : Rungs[next];
            if (isAvailable(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>True if stem (no ".en" suffix) is one of the known ladder rungs.</summary>
    public static bool IsKnownStem(string stem) => Array.IndexOf(Rungs, stem) >= 0;

    /// <summary>True if the stem (no ".en" suffix) has English-only weights available. Only
    /// tiny/base/small/medium ship ".en" variants - large-v3 has none (there is no
    /// ggml-large-v3.en.bin), so the language-lock weight fix-up must not append ".en" for it
    /// (finding I2: doing so faults engine recreate on a nonexistent model file).</summary>
    public static bool HasEnglishVariant(string stem) => stem is "tiny" or "base" or "small" or "medium";
}

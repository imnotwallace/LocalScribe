namespace LocalScribe.Core.Transcription;

/// <summary>One Whisper model as the pickers present it: the canonical technical name (primary
/// and evidentiary - it is what SessionRecord.Model persists), a plain-language subtitle, an
/// accuracy Rank (lower = more accurate; drives "best available on disk" defaults), and whether
/// the weights are English-only. Display metadata PLUS, since Tier 1 T1-6 (spec 2026-08-05
/// :66-72), an EXPORTED fact: the owner ruling froze the live model cap, so the accuracy tier
/// derived from Subtitle is disclosed at Start, in a transcript marker and in export metadata.
/// (This doc previously read "never persisted, never exported"; that is no longer true, and a
/// stale comment is a defect in this codebase.) Rank and EnglishOnly remain display-only.</summary>
public sealed record WhisperModelInfo(string Name, string Subtitle, int Rank, bool EnglishOnly);

/// <summary>The shared catalog behind all three model pickers (Import, Re-transcribe, Settings)
/// - same never-drift rule as LanguageChoice.All. The model set stays OPEN: Describe falls back
/// to a passthrough entry for any name it does not know, mirroring ModelFileResolver's "unknown
/// suffixes stay raw everywhere and load verbatim" rule, so a user-dropped ggml file is always
/// selectable. Copy is qualitative only: no sizes (the real file varies ~2x by backend - f16 on
/// CUDA, quantized on CPU/Vulkan) and no invented benchmark numbers (house precedent: the
/// diariser refuses invented ETAs).</summary>
public static class WhisperModelCatalog
{
    /// <summary>"auto" (Rank -1) is the Settings-only sentinel - never returned by
    /// ModelPaths.AvailableModels, so it can never win a best-Rank default in the dialogs.</summary>
    private static readonly Dictionary<string, WhisperModelInfo> Known = new[]
    {
        new WhisperModelInfo("auto", "Choose automatically for this PC", -1, false),
        new WhisperModelInfo("large-v3-turbo", "Best accuracy at fast speed - recommended", 0, false),
        new WhisperModelInfo("large-v3", "Best accuracy - much slower than the recommended option", 1, false),
        new WhisperModelInfo("medium.en", "Good accuracy, English only - slower", 2, true),
        new WhisperModelInfo("medium", "Good accuracy, any language - slower", 3, false),
        new WhisperModelInfo("small.en", "Decent accuracy, English only - quick", 4, true),
        new WhisperModelInfo("small", "Decent accuracy, any language - quick", 5, false),
        new WhisperModelInfo("base.en", "Basic accuracy, English only - very fast", 6, true),
        new WhisperModelInfo("base", "Basic accuracy, any language - very fast", 7, false),
        new WhisperModelInfo("tiny.en", "Lowest accuracy, English only - fastest, for quick drafts", 8, true),
        new WhisperModelInfo("tiny", "Lowest accuracy, any language - fastest, for quick drafts", 9, false),
    }.ToDictionary(m => m.Name, StringComparer.Ordinal);

    /// <summary>Catalog hit, else a passthrough entry: the name verbatim, no subtitle, worst
    /// Rank (an unknown model must never outrank a cataloged one in a best-available default),
    /// EnglishOnly from the ".en" naming convention.</summary>
    public static WhisperModelInfo Describe(string name)
        => Known.TryGetValue(name, out var info)
            ? info
            : new WhisperModelInfo(name, "", int.MaxValue,
                name.EndsWith(".en", StringComparison.Ordinal));

    /// <summary>The shared picker projection: one entry per name, Ordinal-sorted by Name (the
    /// ordering all three pickers used before the catalog existed).</summary>
    public static IReadOnlyList<WhisperModelInfo> DescribeAll(IEnumerable<string> names)
        => names.OrderBy(n => n, StringComparer.Ordinal).Select(Describe).ToList();

    /// <summary>The accuracy TIER alone: the leading phrase of the catalog subtitle, up to the
    /// first comma or " - " ("Decent accuracy, English only - quick" -> "Decent accuracy"). Empty
    /// for the "auto" sentinel (Rank -1 - an accuracy claim about it would be meaningless) and for
    /// any uncatalogued name (empty Subtitle), so callers must handle "" rather than assume a tier.
    /// DERIVED from Subtitle rather than stored as a fifth record member: WhisperModelInfo is a
    /// POSITIONAL record constructed with four arguments at three sites outside this file
    /// (ImportDialogViewModel, RetranscribeDialogViewModel, SettingsPageViewModel), and a fifth
    /// member would break all three (Tier 1 T1-6).</summary>
    public static string AccuracyTier(string name)
    {
        var info = Describe(name);
        if (info.Rank < 0 || info.Subtitle.Length == 0) return "";
        string s = info.Subtitle;
        int comma = s.IndexOf(',', StringComparison.Ordinal);
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        int cut = comma < 0 ? dash : dash < 0 ? comma : Math.Min(comma, dash);
        return cut < 0 ? s : s[..cut];
    }
}

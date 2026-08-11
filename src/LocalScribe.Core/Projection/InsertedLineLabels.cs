using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>The two fixed conventions a human-authored line depends on (design 2026-08-11 section
/// 2.3), in Core so the projection, the storage layer and the App editors cannot drift.</summary>
public static class InsertedLineLabels
{
    /// <summary>The display name for an inserted line with no participant, or one whose
    /// participant no longer exists in meta.json. A fixed label rather than null: NameResolver
    /// never returns null, so no non-marker DisplayRow has ever carried a null DisplayName and
    /// every renderer, the grouper and TranscriptCitation would be meeting a new state. British
    /// English, like every other user-facing string.</summary>
    public const string Unattributed = "Unattributed";

    private const string Prefix = "i";
    private const int Digits = 6;

    /// <summary>Mint the next insertion id: "i" + a zero-padded ordinal one past the HIGHEST
    /// existing one (not the count - a revert leaves a gap, and reusing a reverted id would make
    /// two different human acts share an identity in the manifest disclosure).
    ///
    /// Zero-padded on purpose: TranscriptProjection's final sort tiebreak compares these ids with
    /// StringComparer.Ordinal, so a fixed width makes ordinal string order EQUAL creation order for
    /// two insertions sharing a timestamp. Past 999999 insertions in one session the width grows
    /// and that equality lapses; the ids stay unique and the sort stays deterministic, which is all
    /// correctness needs. An unparseable key (a hand-edited edits.json) is skipped rather than
    /// throwing - it must never block a person from adding a line.
    ///
    /// NumberStyles.None + InvariantCulture are explicit rather than defaulted: a bare
    /// int.TryParse accepts leading signs and whitespace and follows the current culture, which is
    /// how "Source:id" parsing elsewhere in this repo became culture-dependent.</summary>
    public static string MintId(IEnumerable<string> existingIds)
    {
        int max = 0;
        foreach (string id in existingIds)
        {
            if (!id.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(id.AsSpan(Prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int n) && n > max)
                max = n;
        }
        return Prefix + (max + 1).ToString("D" + Digits, CultureInfo.InvariantCulture);
    }
}

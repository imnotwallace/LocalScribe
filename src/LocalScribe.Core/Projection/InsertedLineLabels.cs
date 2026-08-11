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

    /// <summary>Mint the next insertion id: "i" + a zero-padded ordinal one past the highest ever
    /// minted for this version. <paramref name="highestMinted"/> is the persisted high-water mark
    /// (Edits.HighestInsertionOrdinal, Task 2 review 2026-08-11) and is the authority - a revert
    /// removes the surviving key outright (no tombstones by design), so the mark is the only place
    /// that memory can live, and manifest.json discloses inserted lines BY ID, so reusing a
    /// reverted one would make two different human acts share an identity in the disclosure.
    /// <paramref name="existingIds"/> stays a self-healing FLOOR: a hand-edited edits.json whose
    /// counter is missing or lower than its own keys still mints forward rather than colliding.
    ///
    /// Zero-padded on purpose: TranscriptProjection's final sort tiebreak compares these ids with
    /// StringComparer.Ordinal, so a fixed width makes ordinal string order EQUAL creation order for
    /// two insertions sharing a timestamp. Past 999999 insertions in one session the width grows
    /// and that equality lapses; the ids stay unique and the sort stays deterministic, which is all
    /// correctness needs. An unparseable key (a hand-edited edits.json) is skipped rather than
    /// throwing - it must never block a person from adding a line.</summary>
    public static string MintId(IEnumerable<string> existingIds, int highestMinted)
    {
        int max = highestMinted;
        foreach (string id in existingIds)
            if (OrdinalOf(id) is { } n && n > max)
                max = n;
        return Prefix + (max + 1).ToString("D" + Digits, CultureInfo.InvariantCulture);
    }

    /// <summary>Parse the numeric ordinal out of an insertion id ("i000007" -> 7), or null when
    /// the id does not have this shape - a hand-edited key, most likely. The single parse helper
    /// MintId needs internally, exposed rather than duplicated: EditStore.ApplyInsertionsAsync
    /// also needs it, to advance the high-water mark for a freshly minted id without re-deriving
    /// the same parse a second time.
    ///
    /// NumberStyles.None + InvariantCulture are explicit rather than defaulted: a bare
    /// int.TryParse accepts leading signs and whitespace and follows the current culture, which is
    /// how "Source:id" parsing elsewhere in this repo became culture-dependent.</summary>
    public static int? OrdinalOf(string id)
    {
        if (!id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return int.TryParse(id.AsSpan(Prefix.Length), NumberStyles.None,
            CultureInfo.InvariantCulture, out int n) ? n : null;
    }
}

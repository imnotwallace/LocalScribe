// src/LocalScribe.Core/Search/SearchModels.cs
namespace LocalScribe.Core.Search;

/// <summary>One indexed transcript line (design 2026-07-13 section 2.1): a projected segment of the
/// session's ACTIVE version, in display order. Text is the displayed corrected text (post
/// vocabulary + edits overlay + split expansion); OriginalText is the machine original, stored ONLY
/// where a human correction (edits.json) made it differ - the "(matches original text)" rule.
/// PartIndex disambiguates split children sharing a Seq. Speaker is the resolved display name
/// (NameResolver output for the row the segment rendered into).</summary>
public sealed record SearchLine(int Seq, int PartIndex, long StartMs, string Text,
    string? OriginalText, string Speaker);

/// <summary>Freshness stamps (design 2.1): last-write ticks of the ACTIVE version's
/// transcript.jsonl / edits.json / speakers.json ("v1" resolves to the session root) plus the root
/// meta.json. 0 = file absent. Value equality (record) is the staleness test; the active-version id
/// is stored beside these on the entry.</summary>
public sealed record SearchFreshnessStamps
{
    public long TranscriptTicks { get; init; }
    public long EditsTicks { get; init; }
    public long SpeakersTicks { get; init; }
    public long MetaTicks { get; init; }
}

/// <summary>One session's index entry: session-level fields (id, title, matter ids, date, source
/// app, participant names, active-version id) + per-line entries + freshness stamps.</summary>
public sealed record SearchSessionEntry
{
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public DateTimeOffset StartedAtUtc { get; init; }
    /// <summary>The session's own recorded offset (null for pre-v3 records) so result cards can
    /// render the same session-local date every other surface shows.</summary>
    public int? UtcOffsetMinutes { get; init; }
    public string App { get; init; } = "";
    /// <summary>Named participants from meta.json - speaker-name matching covers these even when
    /// a participant never has a resolved line (design 2.1: participants + overlay names).</summary>
    public IReadOnlyList<string> Participants { get; init; } = [];
    public string VersionId { get; init; } = "v1";
    public SearchFreshnessStamps Stamps { get; init; } = new();
    public IReadOnlyList<SearchLine> Lines { get; init; } = [];
}

/// <summary>The persisted cache shape at storageRoot\index\search-index.json (SchemaGuard-stamped).</summary>
public sealed record SearchIndexCache
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SearchSessionEntry> Sessions { get; init; } = [];
}

/// <summary>A query: free text (whitespace-split into AND terms) + optional facets. FromUtc is
/// inclusive, ToUtc exclusive (callers pass day+1 for an inclusive "To" day); App compares
/// case-insensitively against SearchSessionEntry.App.</summary>
public sealed record SearchQuery(string Text, string? MatterId = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, string? App = null);

/// <summary>One snippet-level hit. Line hits carry the line's Seq/PartIndex/StartMs/Speaker and a
/// ±60-char snippet around the first term occurrence; MatchesOriginalOnly marks a hit found only in
/// the machine original of a corrected line (snippet then comes FROM the original). Speaker-name
/// hits (IsSpeakerNameMatch) snippet the speaker's first line; Seq -1 = a named participant with no
/// resolved line (nothing to scroll to).</summary>
public sealed record SearchHit(int Seq, int PartIndex, long StartMs, string Speaker,
    string Snippet, string MatchedTerm, bool MatchesOriginalOnly, bool IsSpeakerNameMatch);

/// <summary>One matched session: its entry, hits in document order (speaker-name hits appended,
/// ordered by name), and HitCount (= Hits.Count) - the primary rank key.</summary>
public sealed record SearchResult(SearchSessionEntry Session, IReadOnlyList<SearchHit> Hits, int HitCount);

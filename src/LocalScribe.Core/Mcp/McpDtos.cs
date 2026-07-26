namespace LocalScribe.Core.Mcp;

// Serialized with McpJsonOptions.Line (snake_case). ContractVersion == 1 on every envelope.
public sealed record McpSearchHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, string Speaker, int Seq, int PartIndex, long StartMs,
    string Snippet, bool MatchesOriginalOnly, bool IsSpeakerNameMatch);
// UnreadableSessions is the ONLY skip-related number ever surfaced to a client. It counts sessions
// the CALLER IS ENTITLED TO SEE (per the consent allowlist) that failed to build - so a non-zero
// value tells a client its own results may be incomplete, with no leak. It is scoped by reading
// each failed session's meta.json standalone (independently of the rest of the build) and running
// it through the same McpConsentFilter.SessionVisible rule as everything else (see McpCorpus's
// attribution helper). A session whose meta.json is ITSELF unreadable is deliberately EXCLUDED,
// even from this scoped count: without a matter tag its consent visibility can't be checked, and
// counting it anyway would be exactly the corpus-wide leak this design replaced. Do not widen this
// back to McpLexicalCatalog.SkippedSessions (the corpus-wide count) - that one stays a server-side
// stderr diagnostic only (see McpCorpus.CatalogSkippedSessions).
public sealed record McpSearchResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    int TotalHits, int UnreadableSessions, IReadOnlyList<McpSearchHitDto> Hits);

public sealed record McpCoverage(int SessionsEligible, int SessionsCovered, int StaleCount,
    int UnreadableSessions);
public sealed record McpSemanticHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, int StartSeq, int StartPartIndex, long StartMs, float Score,
    string Snippet);
public sealed record McpSemanticResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    McpCoverage Coverage, IReadOnlyList<McpSemanticHitDto> Hits);

public sealed record McpTranscriptRowDto(string Kind, int? Seq, int? PartIndex, long StartMs, long EndMs,
    string? Speaker, string Text);
public sealed record McpReadResponse(int ContractVersion, string SessionId, string VersionId,
    IReadOnlyList<McpTranscriptRowDto> Rows, string? NextCursor);

public sealed record McpSessionDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, long? ApproxDurationMs, bool HasSummary);
public sealed record McpSessionListResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    int Total, int UnreadableSessions, IReadOnlyList<McpSessionDto> Sessions);

public sealed record McpMatterDto(string Id, string Name, string? Reference, int SessionCount);
public sealed record McpMattersResponse(int ContractVersion, IReadOnlyList<McpMatterDto> Matters);

public sealed record McpSummaryResponse(int ContractVersion, string SessionId,
    string ContentMarkdown, DateTimeOffset CreatedAt, string ModelFile, string Backend,
    bool CudaFellToCpu, bool Stale, string SourceTranscriptVersion);

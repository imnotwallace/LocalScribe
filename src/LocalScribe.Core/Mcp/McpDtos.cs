namespace LocalScribe.Core.Mcp;

// Serialized with McpJsonOptions.Line (snake_case). ContractVersion == 1 on every envelope.
public sealed record McpSearchHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, string Speaker, int Seq, int PartIndex, long StartMs,
    string Snippet, bool MatchesOriginalOnly);
// No skipped-session count here by design: a session the catalog failed to parse has no entry,
// so its matter tags - and therefore its consent visibility - are unknowable by construction.
// A count can't be scoped to the consent-visible set, and a corpus-wide count would itself leak
// that non-visible sessions exist. McpLexicalCatalog.SkippedSessions stays server-side diagnostics
// only (logged to stderr) - do not re-add a skipped count to this client-facing contract.
public sealed record McpSearchResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    int TotalHits, IReadOnlyList<McpSearchHitDto> Hits);

public sealed record McpCoverage(int SessionsEligible, int SessionsCovered, int StaleCount);
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
    int Total, IReadOnlyList<McpSessionDto> Sessions);

public sealed record McpMatterDto(string Id, string Name, string? Reference, int SessionCount);
public sealed record McpMattersResponse(int ContractVersion, IReadOnlyList<McpMatterDto> Matters);

public sealed record McpSummaryResponse(int ContractVersion, string SessionId,
    string ContentMarkdown, DateTimeOffset CreatedAt, string ModelFile, string Backend,
    bool CudaFellToCpu, bool Stale, string SourceTranscriptVersion);

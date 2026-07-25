namespace LocalScribe.Core.Mcp;

// Serialized with McpJsonOptions.Line (snake_case). ContractVersion == 1 on every envelope.
public sealed record McpSearchHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, string Speaker, int Seq, int PartIndex, long StartMs,
    string Snippet, bool MatchesOriginalOnly);
public sealed record McpSearchResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    int SkippedSessions, int TotalHits, IReadOnlyList<McpSearchHitDto> Hits);

public sealed record McpCoverage(int SessionsEligible, int SessionsCovered, int StaleCount);
public sealed record McpSemanticHitDto(string SessionId, string Title, string DateLocal, string App,
    IReadOnlyList<string> Matters, int StartSeq, int StartPartIndex, long StartMs, float Score,
    string Snippet);
public sealed record McpSemanticResponse(int ContractVersion, DateTimeOffset IndexAsOfUtc,
    McpCoverage Coverage, IReadOnlyList<McpSemanticHitDto> Hits);

public sealed record McpTranscriptRowDto(string Kind, int? Seq, long StartMs, long EndMs,
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

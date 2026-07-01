namespace LocalScribe.Core.Projection;

/// <summary>Header view-model for transcript.md/.txt (spec section 6).</summary>
public sealed record TranscriptHeader(
    string Title, string App, DateTimeOffset StartedAtLocal, long DurationMs, string Model, string Backend);

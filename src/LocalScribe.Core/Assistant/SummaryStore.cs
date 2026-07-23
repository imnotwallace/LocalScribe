using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>One generated summary (design 2026-07-18 section 7.3 sidecar shape, LOCKED
/// contract - feat/matter-qa reads these for matter-scope context; CudaFellToCpu is a
/// 2026-07-23 ADDITIVE field, absent in older sidecars = false). SourceTranscriptVersion
/// stores SessionRecord.ActiveVersion / LoadedProjection.VersionId at generation time
/// ("v1" or a TranscriptVersion.Id). Model records the file + pinned sha256 + the backend
/// ACTUALLY used (from AssistantDone); CudaFellToCpu records that an "auto" request wanted
/// the GPU and could not fully offload (the recorded, never-silent fall - design 7.7).</summary>
public sealed record SummaryVersion(string Id, DateTimeOffset CreatedAt, string SourceTranscriptVersion,
    AssistantModelRef Model, int PromptVersion, string ContentMarkdown, bool Stale,
    bool CudaFellToCpu = false);

public sealed record SummariesFile
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SummaryVersion> Versions { get; init; } = [];
}

/// <summary>assistant\summaries.json per session: versioned, APPEND-ONLY (design 7.3 -
/// regenerate appends a new version, nothing is overwritten; MarkAllStale flips only the
/// stale flag). SpeakersStore pattern: JsonFile + LocalScribeJson + SchemaGuard, writes
/// atomic via AtomicFile (which also creates the assistant\ folder). The HELPER never
/// writes files - only this App/Core-side store persists assistant artifacts.</summary>
public sealed class SummaryStore(StoragePaths paths)
{
    public const int Version = 1;

    public async Task<IReadOnlyList<SummaryVersion>> LoadAsync(string sessionId, CancellationToken ct)
    {
        string path = paths.SummariesJson(sessionId);
        var obj = await SchemaGuard.ReadObjectAsync(path, ct);
        if (obj is null) return [];
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "summaries.json");
        var file = await JsonFile.ReadAsync<SummariesFile>(path, ct);
        return file?.Versions ?? [];
    }

    public async Task AppendAsync(string sessionId, SummaryVersion version, CancellationToken ct)
    {
        var existing = await LoadAsync(sessionId, ct);
        await SaveAsync(sessionId, [.. existing, version], ct);
    }

    /// <summary>Transcript changed (SessionContentChanged / finalize / re-transcription):
    /// every version goes stale; regeneration stays an explicit user CTA, never automatic
    /// (design 7.3). No-op (no write) when there is nothing to flip.</summary>
    public async Task MarkAllStaleAsync(string sessionId, CancellationToken ct)
    {
        var existing = await LoadAsync(sessionId, ct);
        if (existing.Count == 0 || existing.All(v => v.Stale)) return;
        await SaveAsync(sessionId, existing.Select(v => v with { Stale = true }).ToList(), ct);
    }

    private Task SaveAsync(string sessionId, IReadOnlyList<SummaryVersion> versions, CancellationToken ct)
        => JsonFile.WriteAsync(paths.SummariesJson(sessionId),
            new SummariesFile { SchemaVersion = Version, Versions = versions }, ct);
}

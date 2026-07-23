using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Assistant;

/// <summary>One persisted Q&amp;A exchange (design 2026-07-18 sections 7.3 + 7.5). Lines is the
/// VALIDATED presentation (chips + verdicts) captured at answer time so history renders
/// self-contained; Backend is what AssistantDone actually reported (floor-fall provenance);
/// the coverage lists carry the matter scope's explicit included/omitted/missing disclosure.
/// CudaFellToCpu is a 2026-07-24 ADDITIVE trailing field (absent in older chat logs = false):
/// it records that this turn's warm session LOADED under an "auto" request that could not fully
/// offload and fell to CPU, so a degraded turn is never silently labelled plain "CPU" - the
/// chat mirror of SummaryVersion.CudaFellToCpu (backend=cpu alone cannot tell a fall from a
/// requested-CPU run).</summary>
public sealed record AssistantChatTurn(string Id, DateTimeOffset AskedAtUtc, string Question,
    string AnswerMarkdown, IReadOnlyList<AnswerLine> Lines, string Model, string Backend,
    string PromptVersion, bool ExcerptMode, string? Disclosure,
    IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds,
    IReadOnlyList<string> MissingSummarySessionIds, int UnverifiableClaims,
    bool CudaFellToCpu = false);

/// <summary>The chats.json shape: schema stamp + append-only turn list.</summary>
public sealed record AssistantChatLog
{
    public int SchemaVersion { get; init; } = AssistantChatStore.Version;
    public IReadOnlyList<AssistantChatTurn> Turns { get; init; } = [];
}

/// <summary>Per-scope chat history over AtomicFile (design 7.3): assistant\chats.json in the
/// session folder (session scope) or the matter folder (matter scope). Append-only by
/// construction - no update or delete surface exists. Missing file = empty log; a NEWER
/// schema fails loud (SchemaGuard pattern, same as edits.json).</summary>
public sealed class AssistantChatStore
{
    public const int Version = 1;
    private readonly string _path;

    public AssistantChatStore(string chatsJsonPath) => _path = chatsJsonPath;

    public async Task<AssistantChatLog> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new AssistantChatLog();
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "chats.json");
        return await JsonFile.ReadAsync<AssistantChatLog>(_path, ct) ?? new AssistantChatLog();
    }

    public async Task AppendAsync(AssistantChatTurn turn, CancellationToken ct)
    {
        var log = await LoadAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await JsonFile.WriteAsync(_path,
            log with { SchemaVersion = Version, Turns = [.. log.Turns, turn] }, ct);
    }
}

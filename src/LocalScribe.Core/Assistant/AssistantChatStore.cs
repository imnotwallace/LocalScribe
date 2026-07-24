using System.Text.Json;
using System.Text.Json.Nodes;
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

/// <summary>One named chat thread (design 2026-07-24). Turns are verbatim, append order.
/// Recap is the condensed running summary of the oldest turns that no longer fit the context
/// window (null until the first condense); RecapThroughTurnId is the last turn folded in, so a
/// reopened thread knows where verbatim history resumes. Archived hides the thread from the
/// active selector but keeps it on disk (nothing destroyed).</summary>
public sealed record AssistantChatThread(string Id, string Name, DateTimeOffset CreatedAt,
    bool Archived, string? Recap, string? RecapThroughTurnId, IReadOnlyList<AssistantChatTurn> Turns);

/// <summary>chats.json v2: schema stamp + named threads (design 2026-07-24). v1 was a flat
/// {turns:[...]} single log; LoadAsync migrates that forward to one "Chat 1" thread.</summary>
public sealed record AssistantChatLog
{
    public int SchemaVersion { get; init; } = AssistantChatStore.Version;
    public IReadOnlyList<AssistantChatThread> Chats { get; init; } = [];

    /// <summary>JSON-ignored convenience: the first non-archived thread's turns (the active
    /// thread), or empty if there are no threads yet. For callers/tests that want the active
    /// thread's verbatim turns without walking Chats themselves. [JsonIgnore] is mandatory -
    /// without it STJ would round-trip a bogus "turns" member into the v2 file.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<AssistantChatTurn> Turns => Chats.FirstOrDefault(c => !c.Archived)?.Turns ?? [];
}

/// <summary>Per-scope chat store over AtomicFile: assistant\chats.json in the session or matter
/// folder. v2 (design 2026-07-24): named threads, each append-only in its turns but with mutable
/// thread metadata (name/archived) and a rolling recap - so the whole file is a load-modify-save,
/// not a blind append. A v1 flat log is migrated forward on read (never rewritten until the next
/// save); a NEWER-than-v2 file fails loud (SchemaGuard).</summary>
public sealed class AssistantChatStore
{
    public const int Version = 2;
    public const string MigratedThreadName = "Chat 1";
    private readonly string _path;

    public AssistantChatStore(string chatsJsonPath) => _path = chatsJsonPath;

    public static AssistantChatThread NewThread(string name, DateTimeOffset createdAt)
        => new(Guid.NewGuid().ToString("N"), name, createdAt, Archived: false,
               Recap: null, RecapThroughTurnId: null, Turns: []);

    public async Task<AssistantChatLog> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new AssistantChatLog();
        int version = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(version, Version, "chats.json");
        if (version < Version) return MigrateForward(obj, version);
        return await JsonFile.ReadAsync<AssistantChatLog>(_path, ct) ?? new AssistantChatLog();
    }

    public Task SaveAsync(AssistantChatLog log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return JsonFile.WriteAsync(_path, log with { SchemaVersion = Version }, ct);
    }

    /// <summary>Convenience append: a full load-modify-save that lands the turn in the first
    /// non-archived thread, creating a MigratedThreadName ("Chat 1") thread if the log is empty.
    /// Has no production caller after the threaded AssistantQaService (which uses LoadAsync/
    /// SaveAsync directly); retained as a test-seeding + single-default-thread convenience. Always
    /// a full load-modify-save - v2 threads are never blindly appended to on disk.</summary>
    public async Task AppendAsync(AssistantChatTurn turn, CancellationToken ct)
    {
        var log = await LoadAsync(ct);
        var target = log.Chats.FirstOrDefault(c => !c.Archived);
        AssistantChatThread updated;
        List<AssistantChatThread> chats;
        if (target is null)
        {
            updated = NewThread(MigratedThreadName, turn.AskedAtUtc) with { Turns = [turn] };
            chats = [.. log.Chats, updated];
        }
        else
        {
            updated = target with { Turns = [.. target.Turns, turn] };
            chats = [.. log.Chats];
            chats[chats.IndexOf(target)] = updated;
        }
        await SaveAsync(log with { Chats = chats }, ct);
    }

    /// <summary>v1 {schemaVersion:1, turns:[...]} -> one "Chat 1" thread. Pure; the file is not
    /// rewritten here - the next SaveAsync persists v2 (design 2026-07-24 migration is load-only
    /// until a write). CreatedAt takes the first turn's time, else DateTimeOffset default.</summary>
    private static AssistantChatLog MigrateForward(JsonObject obj, int version)
    {
        if (version != 1)
            throw new InvalidDataException($"chats.json v{version} has no forward migration to v{Version}.");
        var turns = obj["turns"].Deserialize<IReadOnlyList<AssistantChatTurn>>(LocalScribeJson.Options)
                    ?? [];
        var created = turns.Count > 0 ? turns[0].AskedAtUtc : default;
        return new AssistantChatLog
        {
            SchemaVersion = Version,
            Chats = [new AssistantChatThread(Guid.NewGuid().ToString("N"), MigratedThreadName,
                        created, Archived: false, Recap: null, RecapThroughTurnId: null, Turns: turns)],
        };
    }
}

using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>Shared session-folder fixture writer for Core tests: writes session.json,
/// meta.json and transcript.jsonl under a temp StoragePaths root the same way a real capture
/// would, so SearchIndexBuilder.BuildEntryAsync / SessionProjectionLoader.LoadAsync can load the
/// result. Extracted from SearchIndexServiceTests (test(mcp): extract shared TestSessionSeeder) so
/// the Mcp tests (Task 3+) can reuse it without duplicating the fixture format.</summary>
internal static class TestSessionSeeder
{
    /// <summary>Original SearchIndexServiceTests fixture: one session, one segment, speaker "Me".</summary>
    internal static async Task SeedSessionAsync(StoragePaths paths, string id, string text,
        DateTimeOffset? started = null)
    {
        var t0 = started ?? new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0, EndedAtUtc = t0.AddMinutes(5),
            DurationMs = 300_000,
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta { Title = "T-" + id }, default);
        await new TranscriptStore(paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"), default);
    }

    /// <summary>Canonical wrapper used by Mcp tests: one session, one speaker per line,
    /// line i gets StartMs = i * 1000 and Seq = i.</summary>
    internal static void WriteBasicSession(StoragePaths paths, string sessionId, string title,
        string? matterId, DateTimeOffset startedUtc, string app, params string[] lines)
    {
        Directory.CreateDirectory(paths.SessionsDir);
        var appKind = Enum.Parse<AppKind>(app, ignoreCase: true);
        new SessionStore(paths.SessionJson(sessionId)).SaveAsync(new SessionRecord
        {
            Id = sessionId, App = appKind, StartedAtUtc = startedUtc,
            EndedAtUtc = startedUtc.AddMilliseconds(lines.Length * 1000),
            DurationMs = lines.Length * 1000,
        }, default).GetAwaiter().GetResult();

        new MetadataStore(paths.MetaJson(sessionId)).SaveAsync(new SessionMeta
        {
            Title = title,
            MatterIds = matterId is null ? [] : [matterId],
        }, default).GetAwaiter().GetResult();

        var store = new TranscriptStore(paths.TranscriptJsonl(sessionId));
        for (int i = 0; i < lines.Length; i++)
        {
            store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                i * 1000, (i + 1) * 1000, lines[i], "Me"), default).GetAwaiter().GetResult();
        }
    }

    /// <summary>Creates matters/matters.json + matters/{id}/matter.json via MatterStore.</summary>
    internal static void EnsureMatter(StoragePaths paths, string matterId, string name,
        string? reference = null)
        => new LocalScribe.Core.Storage.MatterStore(paths.MattersDir)
            .CreateAsync(new LocalScribe.Core.Model.Matter { Id = matterId, Name = name, Reference = reference })
            .GetAwaiter().GetResult();

    /// <summary>A finalized session with session.json + meta.json but ZERO machine transcript
    /// content (Task 5, design 2026-08-11 section 2.3) - a fixture that hosts nothing but a
    /// human-authored line. EndedAtUtc must be set: EditStore.ApplyInsertionsAsync's
    /// EnsureFinalizedAsync gate requires a finalized (or recovered) session. minDurationMs is the
    /// caller's own bound (usually derived from the insertion's StartMs) rather than 0, so the
    /// insertion write exercises the real upper-bound check instead of the "no duration recorded"
    /// skip path.</summary>
    internal static async Task WriteInsertionOnlySession(StoragePaths paths, string sessionId,
        string title, string? matterId, DateTimeOffset startedUtc, string app, long minDurationMs)
    {
        Directory.CreateDirectory(paths.SessionsDir);
        var appKind = Enum.Parse<AppKind>(app, ignoreCase: true);
        await new SessionStore(paths.SessionJson(sessionId)).SaveAsync(new SessionRecord
        {
            Id = sessionId, App = appKind, StartedAtUtc = startedUtc,
            EndedAtUtc = startedUtc.AddMilliseconds(minDurationMs), DurationMs = minDurationMs,
        }, default);
        await new MetadataStore(paths.MetaJson(sessionId)).SaveAsync(new SessionMeta
        {
            Title = title, MatterIds = matterId is null ? [] : [matterId],
        }, default);
    }

    /// <summary>Writes one human-authored line via EditStore.ApplyInsertionsAsync - the same
    /// non-destructive edits.json overlay the App's insertion UI writes through (Task 5). The
    /// session named by sessionId must already exist and be finalized (WriteInsertionOnlySession /
    /// WriteBasicSession both qualify). Minting is deterministic from a fresh edits.json, so the
    /// first insertion in any given fixture is always "i000001".</summary>
    internal static async Task InsertLineAsync(StoragePaths paths, TimeProvider time,
        string sessionId, string text, long startMs, string? speakerParticipantId = null)
    {
        var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(default);
        await new EditStore(paths.SessionDir(sessionId), time).ApplyInsertionsAsync(
            [new EditStore.InsertedLineWrite(null, text, startMs, speakerParticipantId)],
            [], session!.DurationMs, default);
    }
}

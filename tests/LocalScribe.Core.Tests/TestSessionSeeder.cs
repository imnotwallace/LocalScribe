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
}

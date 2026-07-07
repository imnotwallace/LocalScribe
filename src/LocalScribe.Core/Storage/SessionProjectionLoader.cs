using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Storage;

/// <summary>The stores -> matters -> vocabulary -> projection load half shared by
/// SessionWriter.RegenerateProjectionsAsync, ReadViewViewModel, and the .docx/.zip exporters
/// (Stage 6.3). Pure load + projection; writes nothing. Behavior-preserving extraction of the
/// pipeline that was duplicated verbatim between the writer and the read view - the byte-identity
/// of transcript.md/.txt/session.txt is load-bearing (SessionProjectionLoaderTests + the existing
/// SessionWriter/ReadView tests are the guard).</summary>
public sealed record LoadedProjection(
    SessionRecord Session,
    SessionMeta Meta,
    IReadOnlyList<TranscriptLine> Lines,
    Speakers? Speakers,
    Edits? Edits,
    IReadOnlyDictionary<string, Matter> MattersById,
    IReadOnlyList<string> MatterDisplays,
    DateTimeOffset StartedLocal,
    IReadOnlyList<DisplayRow> Rows,
    TranscriptHeader Header,
    SessionTextView TextView);

public static class SessionProjectionLoader
{
    public static async Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, CancellationToken ct)
    {
        var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct)
                      ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(ct)
                   ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
        var lines = await new TranscriptStore(paths.TranscriptJsonl(sessionId)).ReadAllAsync(ct);
        var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId)).LoadAsync(ct);
        var edits = await new EditStore(paths.SessionDir(sessionId), time).LoadAsync(ct);

        var matterStore = new MatterStore(paths.MattersDir);
        var mattersById = new Dictionary<string, Matter>();
        var matterDisplays = new List<string>();
        foreach (string mid in meta.MatterIds)
        {
            var m = await matterStore.LoadAsync(mid, ct);
            if (m is null) { matterDisplays.Add(mid); continue; }
            mattersById[mid] = m;
            matterDisplays.Add(string.IsNullOrEmpty(m.Reference) ? m.Name : $"{m.Name} ({m.Reference})");
        }

        var projection = new TranscriptProjection(
            new VocabularyProvider(settings.Vocabulary, mattersById), new PhantomBleedDedup());
        var rows = projection.Build(lines, speakers, edits, meta, settings.SectionGapMs);

        var header = new TranscriptHeader(meta.Title, session.App.ToString(), startedLocal,
            session.DurationMs, session.Model, session.Backend);

        var participants = meta.Participants.Select(p =>
            string.IsNullOrEmpty(p.Role) ? $"{p.Name} ({p.Side})" : $"{p.Name} ({p.Role}, {p.Side})").ToList();
        DateTimeOffset? endedLocal = session.EndedAtUtc is DateTimeOffset ended
            ? (session.UtcOffsetMinutes is int endOffsetMin
                ? ended.ToOffset(TimeSpan.FromMinutes(endOffsetMin))
                : ended.ToLocalTime())
            : null;
        var view = new SessionTextView(meta.Title, matterDisplays, participants,
            startedLocal, endedLocal, session.DurationMs,
            MediumDisplay(meta.Medium), meta.Description, Summary: null);

        return new LoadedProjection(session, meta, lines, speakers, edits, mattersById, matterDisplays,
            startedLocal, rows, header, view);
    }

    private static string MediumDisplay(Medium m) => m == Medium.InPerson ? "In-person" : m.ToString();
}

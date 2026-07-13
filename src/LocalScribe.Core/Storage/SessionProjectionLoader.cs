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
    SessionTextView TextView,
    string VersionId);

public static class SessionProjectionLoader
{
    public static Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, CancellationToken ct)
        => LoadAsync(paths, settings, time, sessionId, versionId: null, ct);

    /// <summary>Explicit-version overload (design 2026-07-13 section 3). versionId null follows
    /// session.ActiveVersion; "v1" is the session root; any other id must be recorded in
    /// session.Versions - a caller naming a version explicitly must fail loud rather than
    /// silently read a different transcript.</summary>
    public static async Task<LoadedProjection> LoadAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, string? versionId, CancellationToken ct)
    {
        var session = await new SessionStore(paths.SessionJson(sessionId)).ReadAsync(ct)
                      ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
        string resolved = versionId ?? session.ActiveVersion;
        TranscriptVersion? version = null;
        if (resolved != TranscriptVersions.Root)
            version = session.Versions.FirstOrDefault(v => v.Id == resolved)
                ?? throw new InvalidOperationException(
                    $"transcript version '{resolved}' is not recorded in session.json for {sessionId}");
        // The session's own recorded offset (spec 1.2) keeps projections deterministic and
        // faithful to where the session happened; machine zone only for pre-v3 records.
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        var meta = await new MetadataStore(paths.MetaJson(sessionId)).LoadAsync(ct)
                   ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
        var lines = await new TranscriptStore(paths.TranscriptJsonl(sessionId, resolved)).ReadAllAsync(ct);
        var speakers = await new SpeakersStore(paths.SpeakersJson(sessionId, resolved)).LoadAsync(ct);
        var edits = await new EditStore(paths.SessionDir(sessionId), time,
            contentDir: paths.VersionDir(sessionId, resolved)).LoadAsync(ct);

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

        // Render-layer phantom-bleed dedup (spec 5): non-destructive - JSONL keeps both copies.
        // Defaults are known-conservative (TextOnlyMinSimilarity 0.975, user decision 2026-07-02);
        // tune against the golden corpus before loosening.
        var projection = new TranscriptProjection(
            new VocabularyProvider(settings.Vocabulary, mattersById), new PhantomBleedDedup());
        var rows = projection.Build(lines, speakers, edits, meta, settings.SectionGapMs);

        var header = new TranscriptHeader(meta.Title, session.App.ToString(), startedLocal,
            session.DurationMs, version?.Model ?? session.Model, version?.Backend ?? session.Backend);

        var participants = meta.Participants.Select(p =>
            string.IsNullOrEmpty(p.Role) ? $"{p.Name} ({p.Side})" : $"{p.Name} ({p.Role}, {p.Side})").ToList();
        // Mirror startedLocal: the end time also uses the session's stored offset so the
        // session.txt Date line is deterministic and internally consistent (both endpoints in the
        // same zone), not the rendering machine's zone. Pre-v3 (no offset) falls back to local.
        DateTimeOffset? endedLocal = session.EndedAtUtc is DateTimeOffset ended
            ? (session.UtcOffsetMinutes is int endOffsetMin
                ? ended.ToOffset(TimeSpan.FromMinutes(endOffsetMin))
                : ended.ToLocalTime())
            : null;
        var view = new SessionTextView(meta.Title, matterDisplays, participants,
            startedLocal, endedLocal, session.DurationMs,
            MediumDisplay(meta.Medium), meta.Description, Summary: null);

        return new LoadedProjection(session, meta, lines, speakers, edits, mattersById, matterDisplays,
            startedLocal, rows, header, view, resolved);
    }

    private static string MediumDisplay(Medium m) => m == Medium.InPerson ? "In-person" : m.ToString();
}

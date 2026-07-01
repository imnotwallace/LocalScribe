// src/LocalScribe.Core/Storage/SessionWriter.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Storage;

/// <summary>Regenerates the readable projections (transcript.md/.txt, session.txt) from the JSON
/// truth, and performs per-session crash recovery (spec section 2.1/section 6/Storage format). Pure orchestration
/// over the stores + projection; the launch-time recovery scan is wired in a later stage.</summary>
public sealed class SessionWriter
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly TimeProvider _time;

    public SessionWriter(StoragePaths paths, Settings settings, TimeProvider time)
        => (_paths, _settings, _time) = (paths, settings, time);

    public async Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct)
    {
        var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(ct)
                      ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
        // The session's own recorded offset (spec 1.2) keeps projections deterministic and
        // faithful to where the session happened; machine zone only for pre-v3 records.
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        var meta = await new MetadataStore(_paths.MetaJson(sessionId)).LoadAsync(ct)
                   ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(ct);
        var speakers = await new SpeakersStore(_paths.SpeakersJson(sessionId)).LoadAsync(ct);
        var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(ct);

        // Resolve referenced matters (for vocabulary + the session.txt matter list).
        var matterStore = new MatterStore(_paths.MattersDir);
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
            new VocabularyProvider(_settings.Vocabulary, mattersById), new NoOpDedup());
        var rows = projection.Build(lines, speakers, edits, meta);

        var header = new TranscriptHeader(meta.Title, session.App.ToString(), startedLocal,
            session.DurationMs, session.Model, session.Backend);

        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId),
            MarkdownRenderer.Render(header, rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId),
            PlainTextRenderer.Render(header, rows, _settings.Timestamps), ct);

        var participants = meta.Participants.Select(p =>
            string.IsNullOrEmpty(p.Role) ? $"{p.Name} ({p.Side})" : $"{p.Name} ({p.Role}, {p.Side})").ToList();
        var view = new SessionTextView(meta.Title, matterDisplays, participants,
            startedLocal, session.EndedAtUtc?.ToLocalTime(), session.DurationMs,
            MediumDisplay(meta.Medium), meta.Description, Summary: null);   // summary reserved (Non-goal)
        await AtomicFile.WriteAllTextAsync(_paths.SessionTxt(sessionId), SessionTextRenderer.Render(view), ct);
    }

    public async Task<bool> RecoverIfNeededAsync(string sessionId, CancellationToken ct)
    {
        var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
        var session = await sessionStore.ReadAsync(ct);
        if (session is null || session.EndedAtUtc is not null) return false;   // absent or already finalized

        var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
        var before = await transcript.ReadAllAsync(ct);
        long lastEndMs = before.Count == 0 ? 0 : before.Max(l => l.EndMs);

        await transcript.AppendAsync(
            TranscriptLine.Marker(await transcript.NextSeqAsync(ct), lastEndMs, Markers.RecoveredSession), ct);

        var after = await transcript.ReadAllAsync(ct);
        await sessionStore.SaveAsync(session with
        {
            Recovered = true,
            EndedAtUtc = session.StartedAtUtc.AddMilliseconds(lastEndMs),
            DurationMs = lastEndMs,
            SegmentCount = after.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = after.Count(l => l.Kind == TranscriptKind.Marker),
        }, ct);

        await RegenerateProjectionsAsync(sessionId, ct);
        return true;
    }

    private static string MediumDisplay(Medium m) => m == Medium.InPerson ? "In-person" : m.ToString();
}

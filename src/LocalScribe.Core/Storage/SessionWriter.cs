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
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings, _time, sessionId, ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId),
            MarkdownRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId),
            PlainTextRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.SessionTxt(sessionId),
            SessionTextRenderer.Render(loaded.TextView), ct);
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
}

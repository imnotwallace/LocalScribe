// src/LocalScribe.Core/Storage/SessionWriter.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Diarisation;
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
    // Tier 1B (2026-08-05): optional so all FOURTEEN existing `new SessionWriter(` sites in src\
    // (plus eight more in tests\ - 22 measured against HEAD) keep compiling untouched. Only the
    // recovery site at MaintenanceService.cs:836 passes one.
    // Null = no diagnostics, never a null-ref: every use is `_log?.Write(...)`.
    private readonly IDiagnosticLog? _log;

    public SessionWriter(StoragePaths paths, Settings settings, TimeProvider time,
        IDiagnosticLog? log = null)
        => (_paths, _settings, _time, _log) = (paths, settings, time, log);

    public async Task RegenerateProjectionsAsync(string sessionId, CancellationToken ct)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings, _time, sessionId, ct: ct);
        // Versioned sessions (design 2026-07-13 section 3.1): the transcript projections land
        // INSIDE the active version's folder ("v1" resolves to the session root, preserving the
        // pre-versioning layout byte-for-byte). session.txt is session-level metadata, not
        // transcript content - it always stays at the root. An INACTIVE version's rendered files
        // are never touched, so the v1 originals are immutable while v2+ is active.
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptMd(sessionId, loaded.VersionId),
            MarkdownRenderer.Render(loaded.Header, loaded.Rows, _settings.Timestamps), ct);
        await AtomicFile.WriteAllTextAsync(_paths.TranscriptTxt(sessionId, loaded.VersionId),
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

        // Tier 1B T1-2 (design 2026-08-05): RetainedAudioSources is written ONLY by
        // SessionController.PersistFinalAsync, which runs LAST - after the whole transcription tail
        // drains. Kill the process at any point before that line and session.json still says `[]`
        // (SessionBootstrap never sets the field), which makes real FLACs on disk unreachable from
        // playback, re-transcription, Split Speakers AND import-time speaker detection: all four
        // gate on retained.Contains(kind) BEFORE any File.Exists. Re-derive from what is actually
        // on disk. UNION, never replace - a partially-written record can already carry sources, and
        // a momentarily unreadable leg must never delete one from evidentiary truth.
        var legs = RetainedAudioProbe.Legs(_paths, sessionId);
        var retained = new List<SourceKind>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
            if (session.RetainedAudioSources.Contains(kind) || legs.Any(l => l.Kind == kind))
                retained.Add(kind);

        // MAX across legs, never SUM (RetranscriptionRunner sums because it measures transcription
        // WORK across two sequentially-fed legs; both legs here are sample-aligned to the SAME
        // session clock, so summing would roughly double a two-leg session). 0 means UNKNOWN, not
        // zero-length: a crashed FLAC was never Close()d, so its STREAMINFO total-samples is
        // whatever FlakeWriter left there, and FlacPcmReader.DurationMs also returns 0 for any read
        // failure. Math.Max below therefore degrades to today's transcript-derived duration.
        long audioMs = 0;
        foreach (var leg in legs)
        {
            long probed;
            try { probed = FlacPcmReader.DurationMs(leg.Path); }
            catch { probed = 0; }        // belt and braces: the reader already swallows, but an
                                         // exception escaping here strands the session forever
            if (probed > audioMs) audioMs = probed;
        }
        long durationMs = Math.Max(lastEndMs, audioMs);

        await transcript.AppendAsync(
            TranscriptLine.Marker(await transcript.NextSeqAsync(ct), lastEndMs, Markers.RecoveredSession), ct);

        if (audioMs > lastEndMs)
        {
            // NextSeqAsync re-reads the whole file (max Seq + 1), so a second marker needs a FRESH
            // call - reusing the first seq would collide. Anchored at lastEndMs, the same instant
            // as the recovery marker: the discrepancy is a fact about the whole tail, not an event
            // at the audio's end (where no transcript line exists to sit beside).
            await transcript.AppendAsync(TranscriptLine.Marker(await transcript.NextSeqAsync(ct), lastEndMs,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    Markers.RecoveredAudioBeyondTranscript, Hms(audioMs), Hms(lastEndMs))), ct);
        }

        var after = await transcript.ReadAllAsync(ct);
        await sessionStore.SaveAsync(session with
        {
            Recovered = true,
            EndedAtUtc = session.StartedAtUtc.AddMilliseconds(durationMs),
            DurationMs = durationMs,
            SegmentCount = after.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = after.Count(l => l.Kind == TranscriptKind.Marker),
            RetainedAudioSources = retained,
        }, ct);

        // The id is Mark()-wrapped, the fixed keys and the integers are not (SHARED-CONTRACT
        // section 1a): SessionId.cs:11 mints yyyy-MM-dd_HHmm_{App}_{Slug(title)}, so an id EMBEDS
        // the session title, i.e. the matter/client name.
        _log?.Write("info", "session", "Recovered an unended session",
            $"id={DiagnosticRedaction.Mark(sessionId)} lastEndMs={lastEndMs} audioMs={audioMs} durationMs={durationMs} "
            + $"retained={string.Join(",", retained)}");

        await RegenerateProjectionsAsync(sessionId, ct);
        return true;
    }

    /// <summary>h:mm:ss for a marker, zero-padded, invariant. Written from TOTAL hours rather than
    /// TimeSpan's "hh" custom format specifier, which TRUNCATES the day component instead of
    /// throwing - a 26-hour value would render as 02:00:00 (recorded lesson, export round
    /// 2026-08-04). Recovery durations are bounded by a single call in practice, but a wrong number
    /// in an evidentiary marker is worse than a long one.</summary>
    private static string Hms(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            (int)span.TotalHours, span.Minutes, span.Seconds);
    }
}

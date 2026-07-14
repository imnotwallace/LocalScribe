// src/LocalScribe.Core/Search/SearchIndexBuilder.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Search;

/// <summary>Derives one session's index entry through the SAME load pipeline the read view and the
/// exporters use (SessionProjectionLoader), so the indexed text is exactly the displayed corrected
/// text (vocabulary + edits overlay + split expansion) and the entry follows the session's ACTIVE
/// version automatically (design 2026-07-13 section 2.1). Marker lines are excluded (marker-text
/// search is a design section 1 non-goal). OriginalText is stored only where a HUMAN correction
/// (edits.json, RowSegment.IsCorrected) made it differ - the vocabulary pass alone does not store
/// an original, matching the spec's "where a correction differs" rule. Read-only: throws on an
/// unreadable session; the caller (SearchIndexService) skips + logs it, never blocking others.</summary>
public static class SearchIndexBuilder
{
    public static async Task<SearchSessionEntry> BuildEntryAsync(StoragePaths paths, Settings settings,
        TimeProvider time, string sessionId, CancellationToken ct)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId, ct);
        var lines = new List<SearchLine>();
        foreach (var row in loaded.Rows)
        {
            if (row.IsMarker) continue;                       // marker text is out of scope (design 1)
            foreach (var seg in row.Segments)
                lines.Add(new SearchLine(seg.Seq, seg.PartIndex, seg.StartMs,
                    Text: seg.ProjectedText,
                    OriginalText: seg.IsCorrected
                        && !string.Equals(seg.RawText, seg.ProjectedText, StringComparison.Ordinal)
                        ? seg.RawText : null,
                    Speaker: row.DisplayName ?? ""));
        }
        return new SearchSessionEntry
        {
            SessionId = sessionId,
            Title = loaded.Meta.Title,
            MatterIds = loaded.Meta.MatterIds,
            StartedAtUtc = loaded.Session.StartedAtUtc,
            UtcOffsetMinutes = loaded.Session.UtcOffsetMinutes,
            App = loaded.Session.App.ToString(),
            Participants = loaded.Meta.Participants
                .Where(p => !string.IsNullOrEmpty(p.Name)).Select(p => p.Name).ToList(),
            VersionId = loaded.VersionId,
            Stamps = ComputeStamps(paths, sessionId, loaded.VersionId),
            Lines = lines,
        };
    }

    /// <summary>Freshness stamps (design 2.1): last-write ticks of the ACTIVE version's
    /// transcript.jsonl / edits.json / speakers.json ("v1" resolves to the session root via the
    /// versioned StoragePaths overloads) plus the root meta.json. 0 = file absent.</summary>
    public static SearchFreshnessStamps ComputeStamps(StoragePaths paths, string sessionId, string versionId)
        => new()
        {
            TranscriptTicks = LastWriteTicks(paths.TranscriptJsonl(sessionId, versionId)),
            EditsTicks = LastWriteTicks(paths.EditsJson(sessionId, versionId)),
            SpeakersTicks = LastWriteTicks(paths.SpeakersJson(sessionId, versionId)),
            MetaTicks = LastWriteTicks(paths.MetaJson(sessionId)),
        };

    private static long LastWriteTicks(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
}

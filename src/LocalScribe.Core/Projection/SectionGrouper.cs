namespace LocalScribe.Core.Projection;

/// <summary>Groups ordered pre-rows into display sections (design 5.4 section 4.2). A same-speaker
/// run merges into one section only while the inter-segment silence gap
/// (next.StartMs - runningSectionEndMs) is strictly below gapMs; a gap at/above the threshold, a
/// speaker change, or a marker starts a new section. Pure - no IO, transcript.jsonl untouched.
/// Input MUST already be ordered (StartMs, SourceRank, Seq); this does not re-sort. A late
/// out-of-order/overlapping insert yields a non-positive gap that is below any positive threshold,
/// so it merges safely and the running end takes the max.</summary>
public static class SectionGrouper
{
    public static IReadOnlyList<DisplayRow> Group(IReadOnlyList<PreRow> rows, int gapMs)
    {
        var result = new List<DisplayRow>(rows.Count);
        long sectionEndMs = 0;   // running end of the current same-speaker section
        foreach (var p in rows)
        {
            if (p.IsMarker)
            {
                result.Add(new DisplayRow { IsMarker = true, StartMs = p.StartMs, EndMs = p.EndMs, Text = p.Text });
                continue;
            }

            bool mergeable = result.Count > 0
                && result[^1] is { IsMarker: false } last
                && last.DisplayName == p.Name
                && p.StartMs - sectionEndMs < gapMs;

            if (mergeable)
            {
                var prev = result[^1];
                result[^1] = prev with
                {
                    Text = prev.Text + " " + p.Text,
                    EndMs = Math.Max(prev.EndMs, p.EndMs),
                    Segments = p.Segment is null ? prev.Segments : [.. prev.Segments, p.Segment],
                };
                sectionEndMs = Math.Max(sectionEndMs, p.EndMs);
            }
            else
            {
                result.Add(new DisplayRow { IsMarker = false, StartMs = p.StartMs, EndMs = p.EndMs,
                    DisplayName = p.Name, Text = p.Text,
                    Segments = p.Segment is null ? [] : [p.Segment] });
                sectionEndMs = p.EndMs;
            }
        }
        return result;
    }
}

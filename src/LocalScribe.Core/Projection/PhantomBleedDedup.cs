using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Starting thresholds for the phantom-bleed heuristic (spec section 5). Tune ONLY against
/// the golden corpus (Stage 2b Task 14) - never ad hoc.</summary>
public sealed record PhantomBleedOptions
{
    public int NearWindowMs { get; init; } = 750;
    public double MinSimilarity { get; init; } = 0.85;
    public double MinRmsGapDb { get; init; } = 3.0;
    public double TextOnlyMinSimilarity { get; init; } = 0.975;
}

/// <summary>Render-layer phantom-bleed suppression (spec section 5; design "speakers instead of
/// headphones"). Hides a Local segment that closely matches a near-simultaneous Remote
/// segment when the Local copy is clearly quieter (the bled copy). Non-destructive: JSONL
/// keeps both; genuine overlap (distinct words or comparable energy) is never suppressed.</summary>
public sealed class PhantomBleedDedup : IRenderDedup
{
    private readonly PhantomBleedOptions _o;
    public PhantomBleedDedup(PhantomBleedOptions? options = null) => _o = options ?? new();

    public IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments)
    {
        var remotes = segments.Where(s => s.Source == TranscriptSource.Remote).ToList();
        if (remotes.Count == 0) return segments;

        var kept = new List<ProjectedSegment>(segments.Count);
        foreach (var s in segments)
        {
            if (s.Source == TranscriptSource.Local && remotes.Any(r => IsBleedOf(s, r)))
                continue;                               // hidden at render; JSONL untouched
            kept.Add(s);
        }
        return kept;
    }

    private bool IsBleedOf(ProjectedSegment local, ProjectedSegment remote)
    {
        bool near = local.StartMs < remote.EndMs + _o.NearWindowMs
                 && remote.StartMs - _o.NearWindowMs < local.EndMs;
        if (!near) return false;

        double similarity = TextDistance.NormalizedSimilarity(local.Text, remote.Text);
        double? localRms = local.Line.RmsDb, remoteRms = remote.Line.RmsDb;

        if (localRms is { } lr && remoteRms is { } rr)
            return similarity >= _o.MinSimilarity && lr <= rr - _o.MinRmsGapDb;

        return similarity >= _o.TextOnlyMinSimilarity;  // no energy evidence: stricter text bar
    }
}

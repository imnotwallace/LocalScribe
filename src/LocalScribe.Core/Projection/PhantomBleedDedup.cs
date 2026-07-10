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
/// keeps both; genuine overlap (distinct words or comparable energy) is never suppressed. A
/// human-corrected OR human-split segment (spec 6.1 step 4, design §2.2) is always exempt from suppression.</summary>
public sealed class PhantomBleedDedup : IRenderDedup
{
    private readonly PhantomBleedOptions _o;
    public PhantomBleedDedup(PhantomBleedOptions? options = null) => _o = options ?? new();

    public IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments)
    {
        var remotes = segments.Where(s => s.Source == TranscriptSource.Remote).ToList();
        var locals = segments.Where(s => s.Source == TranscriptSource.Local).ToList();
        if (remotes.Count == 0 || locals.Count == 0) return segments;

        // Pass 1 (classic direction, unchanged semantics): a quieter Local copy of a
        // near-simultaneous Remote is the bled copy - hide it.
        var hiddenLocals = new HashSet<ProjectedSegment>(
            locals.Where(s => !(s.Corrected || s.IsSplitChild) && remotes.Any(r => IsBleedOf(s, r))));

        // Pass 2 (2026-07-10, design section 4): the user's own voice echoed back on the Remote
        // leg. Only anchor locals (below) can hide a remote, so a matching pair can never vanish
        // entirely; RMS evidence is REQUIRED in this direction (a genuine remote speaker
        // repeating the words has comparable energy and must survive).
        //
        // Pass-2 anchors (2026-07-11, spec 6.1 step 4): only locals with NO bleed match anchor a
        // remote-hide. A bleed-matched local is either hidden by pass 1 (non-exempt) or was kept
        // solely by the corrected/split exemption - and a human correction un-hides the PAIR; the
        // auto-dedup must not re-hide the other copy. A corrected local that survives on its own
        // evidence still anchors: correcting your own line does not rescue its echo.
        var anchorLocals = locals.Where(l => !remotes.Any(r => IsBleedOf(l, r))).ToList();

        var kept = new List<ProjectedSegment>(segments.Count);
        foreach (var s in segments)
        {
            if (s.Source == TranscriptSource.Local && hiddenLocals.Contains(s)) continue;
            if (s.Source == TranscriptSource.Remote && !(s.Corrected || s.IsSplitChild)
                && anchorLocals.Any(l => IsEchoOfLocal(s, l)))
                continue;                                   // hidden at render; JSONL untouched
            kept.Add(s);
        }
        return kept;
    }

    /// <summary>Echo-copy similarity: whole-string OR best-containment (an echo leg often picks up
    /// extra surrounding tokens, which whole-string distance over-punishes). Threshold VALUES are
    /// unchanged - tune ONLY against the golden corpus.</summary>
    private static double Similarity(string a, string b)
        => Math.Max(TextDistance.NormalizedSimilarity(a, b), TextDistance.ContainmentSimilarity(a, b));

    private bool IsBleedOf(ProjectedSegment local, ProjectedSegment remote)
    {
        bool near = local.StartMs < remote.EndMs + _o.NearWindowMs
                 && remote.StartMs - _o.NearWindowMs < local.EndMs;
        if (!near) return false;

        double similarity = Similarity(local.Text, remote.Text);
        double? localRms = local.Line.RmsDb, remoteRms = remote.Line.RmsDb;

        if (localRms is { } lr && remoteRms is { } rr)
            return similarity >= _o.MinSimilarity && lr <= rr - _o.MinRmsGapDb;

        return similarity >= _o.TextOnlyMinSimilarity;  // no energy evidence: stricter text bar
    }

    private bool IsEchoOfLocal(ProjectedSegment remote, ProjectedSegment local)
    {
        bool near = remote.StartMs < local.EndMs + _o.NearWindowMs
                 && local.StartMs - _o.NearWindowMs < remote.EndMs;
        if (!near) return false;
        if (local.Line.RmsDb is not { } lr || remote.Line.RmsDb is not { } rr) return false;
        return Similarity(remote.Text, local.Text) >= _o.MinSimilarity
            && Math.Abs(lr - rr) >= _o.MinRmsGapDb;
    }
}

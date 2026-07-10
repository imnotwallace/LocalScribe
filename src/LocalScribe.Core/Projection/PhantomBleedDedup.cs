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

    /// <summary>NEW mechanism constant (2026-07-11 user decision) - not one of the four original
    /// golden-corpus-gated values above, which remain untouched: a containment-driven hide (either
    /// pass/direction) additionally requires the pair's MUTUAL time coverage - overlap divided by
    /// the LONGER of the two durations - to be at least this fraction. Rationale: an echo/bleed is
    /// the SAME sound, so the two copies occupy nearly the same time span; a different utterance
    /// that merely shares tokens does not. Closes the fragment-shadowing false positive in both
    /// roles (a short louder fragment containment-matching a longer genuine line it only briefly
    /// overlaps - whether the fragment is the would-be keeper or the would-be hidden side).
    /// Whole-string similarity is never subject to this guard.</summary>
    public double EchoTimeCoverageMin { get; init; } = 0.70;
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

    /// <summary>Mutual time coverage of a candidate hide pair (2026-07-11 user decision - the echo
    /// time-coverage guard): overlap / max(duration a, duration b), i.e. the overlap must cover
    /// most of BOTH segments' spans. An echo/bleed is the SAME sound, so the two copies are nearly
    /// coextensive in time; a brief fragment sharing tokens with a longer line covers little of it
    /// - in WHICHEVER role. The symmetric form matters: checking only the hidden side's coverage
    /// would let pass 2 hide a genuine SHORT remote interjection that sits fully inside a longer
    /// local's span (its own coverage is trivially 1.0 while it covers little of the anchor).
    /// Zero-or-negative max duration yields 0 (guards the division; a degenerate span offers no
    /// coverage evidence).</summary>
    private static double EchoTimeCoverage(ProjectedSegment a, ProjectedSegment b)
    {
        long maxDuration = Math.Max(a.EndMs - a.StartMs, b.EndMs - b.StartMs);
        if (maxDuration <= 0) return 0.0;
        long overlap = Math.Max(0L, Math.Min(a.EndMs, b.EndMs) - Math.Max(a.StartMs, b.StartMs));
        return overlap / (double)maxDuration;
    }

    /// <summary>Echo-copy similarity for a candidate hide: whole-string, raised to best-containment
    /// (an echo leg often picks up extra surrounding tokens, which whole-string distance
    /// over-punishes) ONLY when the containment guards hold. Whole-string similarity alone is never
    /// subject to any guard. Threshold VALUES are unchanged - tune ONLY against the golden corpus.
    ///
    /// Containment guards:
    /// - Time coverage (both passes, 2026-07-11 user decision): the pair's mutual time coverage
    ///   (see <see cref="EchoTimeCoverage"/>) must be at least EchoTimeCoverageMin - an echo is the
    ///   same sound and is nearly coextensive with its counterpart; a fragment that merely shares
    ///   tokens is not.
    /// - Direction (pass 1 only, via <paramref name="hiddenMustBeContainer"/>): the hidden local
    ///   must be the container/longer side - the designed pass-1 case is a bled copy that picked up
    ///   EXTRA tokens; a shorter genuine local remark must never be swallowed by a longer remote
    ///   (attribution). Equal normalized lengths keep containment - it degenerates to the
    ///   whole-string comparison anyway.</summary>
    private double GuardedSimilarity(ProjectedSegment hidden, ProjectedSegment keeper,
        bool hiddenMustBeContainer)
    {
        double similarity = TextDistance.NormalizedSimilarity(hidden.Text, keeper.Text);
        bool directionOk = !hiddenMustBeContainer
            || TextDistance.Normalize(hidden.Text).Length >= TextDistance.Normalize(keeper.Text).Length;
        if (directionOk && EchoTimeCoverage(hidden, keeper) >= _o.EchoTimeCoverageMin)
            similarity = Math.Max(similarity, TextDistance.ContainmentSimilarity(hidden.Text, keeper.Text));
        return similarity;
    }

    private bool IsBleedOf(ProjectedSegment local, ProjectedSegment remote)
    {
        bool near = local.StartMs < remote.EndMs + _o.NearWindowMs
                 && remote.StartMs - _o.NearWindowMs < local.EndMs;
        if (!near) return false;

        double similarity = GuardedSimilarity(hidden: local, keeper: remote, hiddenMustBeContainer: true);
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
        return GuardedSimilarity(hidden: remote, keeper: local, hiddenMustBeContainer: false) >= _o.MinSimilarity
            && Math.Abs(lr - rr) >= _o.MinRmsGapDb;
    }
}

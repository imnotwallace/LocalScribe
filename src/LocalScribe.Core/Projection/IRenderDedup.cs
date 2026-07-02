namespace LocalScribe.Core.Projection;

/// <summary>Apply-order step 4 (spec section 5/section 6.1): MAY hide phantom-bleed segments. Stage 2a ships the
/// no-op; the real energy/text heuristic lands in Stage 2b where golden-corpus data exists to tune it.</summary>
public interface IRenderDedup
{
    IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments);
}

public sealed class NoOpDedup : IRenderDedup
{
    public IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments) => segments;
}

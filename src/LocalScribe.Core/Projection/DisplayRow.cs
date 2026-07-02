namespace LocalScribe.Core.Projection;

/// <summary>One rendered row: a grouped speaker turn (IsMarker=false, DisplayName set) or a
/// standalone system marker (IsMarker=true, DisplayName null).</summary>
public sealed record DisplayRow
{
    public bool IsMarker { get; init; }
    public long StartMs { get; init; }
    public string? DisplayName { get; init; }
    public string Text { get; init; } = "";
}

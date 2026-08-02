using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

/// <summary>Per-segment wrapper around a Core <see cref="RowSegment"/> for the read view's prose
/// inlines (ITEM 5, 2026-08-01). Adds a moving IsNowPlaying flag the SegmentText behavior tints on
/// the exact segment under the playhead - the same decoupled-from-selection pattern as
/// <see cref="ReadRow.IsNowPlaying"/>. IsEstimatedStart is true for a split child, whose start is a
/// character-proportion estimate, never a real token time. RowSegment stays untouched (canonical
/// projection payload).</summary>
public sealed partial class ReadSegment : ObservableObject
{
    public RowSegment Data { get; }
    [ObservableProperty] private bool _isNowPlaying;

    public long StartMs => Data.StartMs;
    public long EndMs => Data.EndMs;
    public string Text => Data.ProjectedText;
    public bool IsEstimatedStart => Data.IsSplitChild;

    public ReadSegment(RowSegment data) => Data = data;
}

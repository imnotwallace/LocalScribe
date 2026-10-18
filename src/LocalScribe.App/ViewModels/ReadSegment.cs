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

    /// <summary>True when this segment's start is a HUMAN ESTIMATE rather than a machine
    /// measurement: a split child's derived boundary, or a whole human-authored line (design
    /// 2026-08-11 section 2.3), whose timestamp is estimated by definition. Drives the
    /// "~[stamp] (estimated)" tooltip in SegmentText. App-side only: the exported document does
    /// NOT carry a per-line estimate mark - that is a per-turn mark, the category Part 3 deleted
    /// outright when it removed "[text corrected]".</summary>
    public bool IsEstimatedStart => Data.IsSplitChild || Data.InsertionId is not null;

    public ReadSegment(RowSegment data) => Data = data;
}

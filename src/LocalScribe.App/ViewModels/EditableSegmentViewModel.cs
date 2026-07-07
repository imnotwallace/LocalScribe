// src/LocalScribe.App/ViewModels/EditableSegmentViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>One editable transcript segment sub-row in Edit mode (design §3.2). Materialized only
/// while its section is being edited. A split child carries a PartIndex and a derived start; a
/// whole (unsplit) segment has PartIndex 0 and DerivedStart false.</summary>
public sealed partial class EditableSegmentViewModel : ObservableObject
{
    public int Seq { get; }
    public TranscriptSource Source { get; }
    public int PartIndex { get; }
    public string RawText { get; }
    /// <summary>The seeded displayed text (post vocabulary + edits) at BeginEdit. Immutable
    /// baseline for the correction no-op guard — comparing against RawText would misfire when the
    /// vocabulary pass changed the text (a phantom "correction" on a line the human never touched).</summary>
    public string ProjectedText { get; }
    public bool IsSplitChild { get; }
    public bool DerivedStart { get; }
    [ObservableProperty] private string _editedText;
    [ObservableProperty] private long _startMs;
    [ObservableProperty] private SpeakerChoice? _speaker;

    public EditableSegmentViewModel(int seq, TranscriptSource source, int partIndex, string editedText,
        long startMs, bool derivedStart, string rawText, SpeakerChoice? speaker, bool isSplitChild)
    {
        (Seq, Source, PartIndex, RawText, ProjectedText, DerivedStart, IsSplitChild)
            = (seq, source, partIndex, rawText, editedText, derivedStart, isSplitChild);
        (_editedText, _startMs, _speaker) = (editedText, startMs, speaker);
    }

    /// <summary>Partition this segment's text at the caret into two parts (design §3.3). The left
    /// part keeps this segment's start; the right part's start is estimated by character
    /// proportion across [StartMs, segEndMs] and snapped to a 10 ms grid. Throws on a degenerate
    /// caret (start/end) that would produce an empty child.</summary>
    public static (SplitPartEdit Left, SplitPartEdit Right) SplitAt(EditableSegmentViewModel seg,
        int caret, long segEndMs)
    {
        string text = seg.EditedText;
        if (caret <= 0 || caret >= text.Length || string.IsNullOrWhiteSpace(text[..caret])
            || string.IsNullOrWhiteSpace(text[caret..]))
            throw new InvalidOperationException("split point would create an empty part.");

        double proportion = (double)caret / text.Length;
        long raw = seg.StartMs + (long)Math.Round(proportion * (segEndMs - seg.StartMs));
        long derived = (long)Math.Round(raw / 10.0) * 10;            // 10 ms grid
        derived = Math.Clamp(derived, seg.StartMs + 10, segEndMs);   // stay strictly after the start

        var left = new SplitPartEdit(text[..caret], seg.StartMs, seg.DerivedStart,
            seg.Speaker?.ParticipantId, seg.Speaker?.ClusterKey);
        var right = new SplitPartEdit(text[caret..], derived, DerivedStart: true,
            seg.Speaker?.ParticipantId, seg.Speaker?.ClusterKey);
        return (left, right);
    }
}

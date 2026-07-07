// src/LocalScribe.App/ViewModels/EditableSectionViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

/// <summary>One section row in Edit mode (design §3.2). Read-only until BeginEdit materializes its
/// constituent segments as EditableSegmentViewModels; splitting replaces a segment with two
/// halves. Edit state lives here (on the VM), so the virtualized list can recycle containers
/// safely — the template is data-triggered off IsEditing.</summary>
public sealed partial class EditableSectionViewModel : ObservableObject
{
    public DisplayRow Row { get; }
    public ObservableCollection<EditableSegmentViewModel> Segments { get; } = new();
    [ObservableProperty] private bool _isEditing;

    private readonly HashSet<int> _splitReverts = new();

    public EditableSectionViewModel(DisplayRow row) => Row = row;

    public void BeginEdit(string timestampsMode, DateTimeOffset startedAt)
    {
        if (IsEditing) return;
        Segments.Clear();
        foreach (var s in Row.Segments)
            Segments.Add(new EditableSegmentViewModel(s.Seq, s.Source, s.PartIndex,
                s.ProjectedText, s.StartMs, derivedStart: s.PartIndex > 0, s.RawText,
                speaker: null, isSplitChild: s.IsSplitChild));
        IsEditing = true;
    }

    public void SplitSegment(EditableSegmentViewModel seg, int caret)
    {
        int i = Segments.IndexOf(seg);
        if (i < 0) return;
        long segEndMs = i + 1 < Segments.Count ? Segments[i + 1].StartMs : Row.EndMs;
        var (left, right) = EditableSegmentViewModel.SplitAt(seg, caret, segEndMs);
        Segments[i] = ToSegment(seg.Seq, seg.Source, i, left, seg.RawText);
        Segments.Insert(i + 1, ToSegment(seg.Seq, seg.Source, i + 1, right, seg.RawText));
        Reindex();
    }

    public void RevertSplit(int seq)
    {
        _splitReverts.Add(seq);
        // Collapse this seq's children back to a single read-of-machine-original segment.
        for (int i = Segments.Count - 1; i >= 0; i--)
            if (Segments[i].Seq == seq && Segments[i].PartIndex > 0) Segments.RemoveAt(i);
        Reindex();
    }

    /// <summary>Splits to persist: any seq that now has >1 part in this section.</summary>
    public IReadOnlyList<SplitEdit> CollectSplits()
        => Segments.GroupBy(s => s.Seq)
            .Where(g => g.Count() > 1)
            .Select(g => new SplitEdit(g.Key, g.First().Source,
                g.OrderBy(s => s.PartIndex).Select(s => new SplitPartEdit(
                    s.EditedText, s.StartMs, s.PartIndex > 0,
                    s.Speaker?.ParticipantId, s.Speaker?.ClusterKey)).ToList()))
            .ToList();

    public IReadOnlyCollection<int> CollectSplitReverts() => _splitReverts.ToList();

    /// <summary>Corrections to persist: unsplit segments whose text changed from the seeded
    /// projected text. Comparing against ProjectedText (not RawText) matches the Stage 6.1
    /// correction dialog's no-op guard, so a vocabulary-only difference never writes a phantom edit.</summary>
    public IReadOnlyDictionary<int, string> CollectCorrections()
        => Segments.Where(s => !s.IsSplitChild)
            .GroupBy(s => s.Seq).Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .Where(s => s.EditedText.Trim() != s.ProjectedText.Trim())
            .ToDictionary(s => s.Seq, s => s.EditedText.Trim());

    private static EditableSegmentViewModel ToSegment(int seq, Core.Model.TranscriptSource source,
        int partIndex, SplitPartEdit part, string rawText)
        => new(seq, source, partIndex, part.Text, part.StartMs, part.DerivedStart, rawText,
            speaker: null, isSplitChild: true);

    private void Reindex()
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            if (s.PartIndex != i)
                Segments[i] = new EditableSegmentViewModel(s.Seq, s.Source, i, s.EditedText, s.StartMs,
                    derivedStart: i > 0, s.RawText, s.Speaker, isSplitChild: Segments.Count > 1 || s.IsSplitChild);
        }
    }
}

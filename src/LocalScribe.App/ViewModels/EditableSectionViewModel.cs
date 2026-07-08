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
    // Task 15: per-source candidate lists for this section's edit session, set by BeginEdit and
    // consulted by ChoicesFor whenever a segment is (re)materialized (initial BeginEdit, split,
    // reindex). Defaulted to empty so the existing 2-arg BeginEdit call sites (pre-Task 15 tests,
    // and any caller that doesn't care about the dropdown) keep compiling unchanged.
    private IReadOnlyList<SpeakerChoice> _remoteChoices = [];
    private IReadOnlyList<SpeakerChoice> _localChoices = [];

    public EditableSectionViewModel(DisplayRow row) => Row = row;

    public void BeginEdit(string timestampsMode, DateTimeOffset startedAt,
        IReadOnlyList<SpeakerChoice>? remoteChoices = null, IReadOnlyList<SpeakerChoice>? localChoices = null,
        Func<int, Core.Model.TranscriptSource, IReadOnlyList<SpeakerChoice>, SpeakerChoice?>? currentSpeaker = null)
    {
        if (IsEditing) return;
        _remoteChoices = remoteChoices ?? [];
        _localChoices = localChoices ?? [];
        Segments.Clear();
        foreach (var s in Row.Segments)
        {
            var choices = ChoicesFor(s.Source);
            // Pre-select the line's CURRENT speaker so the dropdown shows what's already there
            // instead of blanking. Split children carry their speaker in the split part (not a
            // whole-segment pin), so they aren't pre-selected from the pin store.
            var speaker = s.IsSplitChild ? null : currentSpeaker?.Invoke(s.Seq, s.Source, choices);
            Segments.Add(new EditableSegmentViewModel(s.Seq, s.Source, s.PartIndex,
                s.ProjectedText, s.StartMs, s.EndMs, derivedStart: s.PartIndex > 0, s.RawText,
                speaker: speaker, isSplitChild: s.IsSplitChild, choices));
        }
        IsEditing = true;
    }

    private IReadOnlyList<SpeakerChoice> ChoicesFor(Core.Model.TranscriptSource source)
        => source == Core.Model.TranscriptSource.Local ? _localChoices : _remoteChoices;

    /// <summary>Task 17 live roster sync (design section 4): replaces this section's stored
    /// per-source candidate lists AND re-threads them into every ALREADY-MATERIALIZED segment's
    /// settable SpeakerChoices - so an open dropdown shows a Session Details rename/add without
    /// losing in-progress EditedText/Speaker/split state, and any FUTURE split in this section
    /// (ToSegment/Reindex both call ChoicesFor) also sees the fresh lists rather than the stale
    /// ones captured at BeginEdit.</summary>
    public void RefreshSpeakerChoices(IReadOnlyList<SpeakerChoice> remoteChoices,
        IReadOnlyList<SpeakerChoice> localChoices)
    {
        _remoteChoices = remoteChoices;
        _localChoices = localChoices;
        foreach (var seg in Segments)
        {
            var newChoices = ChoicesFor(seg.Source);
            seg.SpeakerChoices = newChoices;
            // Re-point a live selection at the value-equal choice in the fresh list, matched by
            // TARGET (participant id / cluster key / unassign) not display text - so a Session
            // Details RENAME (same id, new name) keeps the selection instead of blanking it. A
            // REMOVED participant has no match; fall back to the visible "Automatic (Me / Them)"
            // rather than a confusing blank (its owner is gone, so the line is heading to baseline).
            if (seg.Speaker is { } cur)
                seg.Speaker = newChoices.FirstOrDefault(c => c.IsUnassign == cur.IsUnassign
                        && c.ParticipantId == cur.ParticipantId && c.ClusterKey == cur.ClusterKey)
                    ?? newChoices.FirstOrDefault(c => c.IsUnassign);
        }
    }

    public void SplitSegment(EditableSegmentViewModel seg, int caret)
    {
        int i = Segments.IndexOf(seg);
        if (i < 0) return;
        // Bug fix (final review): the ceiling MUST be this segment's own machine EndMs, not the
        // next segment's StartMs. A section can hold several same-speaker machine segments
        // separated by a silence gap (SectionGrouper's sectionGapMs), so the next segment's start
        // can sit past this one's own end - splitting against it could derive an estimate beyond
        // line.EndMs and blow up EditStore.ApplySplitAsync's range check.
        var (left, right) = EditableSegmentViewModel.SplitAt(seg, caret, seg.EndMs);
        // The left half's own end becomes the derived boundary (the right half's start); the right
        // half keeps the original segment's own end - so a further split of either half is still
        // correctly bounded.
        Segments[i] = ToSegment(seg.Seq, seg.Source, i, left, seg.RawText, endMs: right.StartMs);
        Segments.Insert(i + 1, ToSegment(seg.Seq, seg.Source, i + 1, right, seg.RawText, endMs: seg.EndMs));
        Reindex();
    }

    public void RevertSplit(int seq)
    {
        _splitReverts.Add(seq);
        // Collapse this seq's parts back to the SINGLE machine-original segment. Deleting only the
        // PartIndex>0 parts was wrong: the surviving part 0 kept its truncated split text ("First.")
        // and the derived boundary as its end, so the row showed a fragment and the other part's
        // text appeared to vanish. Restore the machine floor instead - the full original text and
        // the machine start/end. Every part carries the machine original in RawText; part 0 holds
        // the machine start and the last part holds the machine end.
        var parts = Segments.Where(s => s.Seq == seq).OrderBy(s => s.PartIndex).ToList();
        if (parts.Count == 0) return;
        int insertAt = Segments.IndexOf(parts[0]);
        var source = parts[0].Source;
        long machineStart = parts[0].StartMs;
        long machineEnd = parts[^1].EndMs;
        string machineText = parts[0].RawText;
        // Prefer the loaded DisplayRow's projected text for this seq when it is still a single
        // unsplit segment there (the common in-session split-then-revert), so a vocabulary-applied
        // render survives; fall back to the machine text for a persisted split (whose original
        // single row isn't present in Row.Segments).
        var loaded = Row.Segments.Where(s => s.Seq == seq).ToList();
        string projected = loaded.Count == 1 ? loaded[0].ProjectedText : machineText;

        for (int i = Segments.Count - 1; i >= 0; i--)
            if (Segments[i].Seq == seq) Segments.RemoveAt(i);
        Segments.Insert(insertAt, new EditableSegmentViewModel(seq, source, 0, projected,
            machineStart, machineEnd, derivedStart: false, machineText, speaker: null,
            isSplitChild: false, ChoicesFor(source)));
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

    private EditableSegmentViewModel ToSegment(int seq, Core.Model.TranscriptSource source,
        int partIndex, SplitPartEdit part, string rawText, long endMs)
        => new(seq, source, partIndex, part.Text, part.StartMs, endMs, part.DerivedStart, rawText,
            speaker: null, isSplitChild: true, ChoicesFor(source));

    private void Reindex()
    {
        // PartIndex and split-child status are PER-SEQ, not per whole-section list position: a
        // section can contain several distinct machine segments (the grouper merges same-speaker
        // turns), so splitting one seq must never relabel the others. A seq with a single member is
        // an unsplit segment (IsSplitChild false, PartIndex 0) and stays collectible as a correction.
        var countBySeq = Segments.GroupBy(s => s.Seq).ToDictionary(g => g.Key, g => g.Count());
        var nextPartBySeq = new Dictionary<int, int>();
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            int partIndex = nextPartBySeq.TryGetValue(s.Seq, out int p) ? p : 0;
            nextPartBySeq[s.Seq] = partIndex + 1;
            bool isSplitChild = countBySeq[s.Seq] > 1;
            if (s.PartIndex != partIndex || s.IsSplitChild != isSplitChild)
                Segments[i] = new EditableSegmentViewModel(s.Seq, s.Source, partIndex, s.EditedText,
                    s.StartMs, s.EndMs, derivedStart: partIndex > 0, s.RawText, s.Speaker, isSplitChild: isSplitChild,
                    s.SpeakerChoices);
        }
    }
}

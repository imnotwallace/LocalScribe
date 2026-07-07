using LocalScribe.Core.Model;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Projection;

/// <summary>The canonical projection apply-order (spec section 6.1) shared by transcript.md/.txt,
/// session.txt, live view, and .docx. Pure - no IO.</summary>
public sealed class TranscriptProjection
{
    private readonly IVocabularyProvider _vocab;
    private readonly IRenderDedup _dedup;
    public TranscriptProjection(IVocabularyProvider vocab, IRenderDedup dedup) => (_vocab, _dedup) = (vocab, dedup);

    public IReadOnlyList<DisplayRow> Build(
        IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta,
        int sectionGapMs = 5000)
    {
        var matterIds = meta.MatterIds;

        // (1)-(3): partition; vocabulary pass; edits overlay (human verbatim wins); SPLIT expansion.
        var projected = new List<ProjectedSegment>();
        var markers = new List<TranscriptLine>();
        foreach (var line in lines)
        {
            if (line.Kind == TranscriptKind.Marker) { markers.Add(line); continue; }
            string text = _vocab.ApplyCorrections(line.Text, matterIds);

            if (edits is not null && edits.Splits.TryGetValue(line.Seq.ToString(), out var split))
            {
                for (int i = 0; i < split.Parts.Count; i++)
                {
                    var part = split.Parts[i];
                    long endMs = i + 1 < split.Parts.Count ? split.Parts[i + 1].StartMs : line.EndMs;
                    projected.Add(new ProjectedSegment(line, part.Text, Corrected: false,
                        IsSplitChild: true, PartIndex: i,
                        StartMsOverride: part.StartMs, EndMsOverride: endMs,
                        SpeakerParticipantId: part.SpeakerParticipantId,
                        SpeakerClusterKey: part.SpeakerClusterKey));
                }
                continue;
            }

            Correction? c = null;
            bool corrected = edits is not null && edits.Corrections.TryGetValue(line.Seq.ToString(), out c);
            if (corrected) text = c!.Text;
            projected.Add(new ProjectedSegment(line, text, corrected));
        }

        // (4): dedup.
        var kept = _dedup.Filter(projected);

        // (5): name resolution -> flat pre-rows for segments and markers. Each segment also gets
        // its Stage 6.1 identity payload (RowSegment) so grouped rows stay per-seq addressable
        // for corrections/pins; dedup-dropped segments never reach here (invisible => not
        // editable from the read view, an accepted Stage 6 quirk).
        var pinnedBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (speakers is not null)
            foreach (var (src, seqs) in speakers.Pinned)
                pinnedBySource[src] = new HashSet<string>(seqs, StringComparer.Ordinal);

        var pre = new List<PreRow>();
        foreach (var s in kept)
        {
            bool corrected = s.Corrected;
            bool pinned = pinnedBySource.TryGetValue(s.Source.ToString(), out var pins)
                && pins.Contains(s.Seq.ToString());
            string name = NameResolver.Resolve(s.Line, speakers, meta,
                s.SpeakerParticipantId, s.SpeakerClusterKey);
            pre.Add(new PreRow(s.StartMs, s.EndMs, Rank(s.Source), s.Seq, name, s.Text, IsMarker: false,
                Segment: new RowSegment(s.Seq, s.Source, s.StartMs, s.EndMs,
                    ProjectedText: s.Text, RawText: s.Line.Text, corrected, pinned,
                    IsSplitChild: s.IsSplitChild, PartIndex: s.PartIndex)));
        }
        foreach (var m in markers)
            pre.Add(new PreRow(m.StartMs, m.EndMs, Rank(m.Source), m.Seq, Name: null, m.Text, IsMarker: true));

        // (6): order (startMs, source rank, seq) then group by speaker + silence gap.
        pre.Sort((a, b) =>
        {
            int c = a.StartMs.CompareTo(b.StartMs);
            if (c != 0) return c;
            c = a.SourceRank.CompareTo(b.SourceRank);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        });
        return SectionGrouper.Group(pre, sectionGapMs);
    }

    private static int Rank(TranscriptSource s) => s switch
    {
        TranscriptSource.Local => 0,
        TranscriptSource.Remote => 1,
        _ => 2,   // System (markers)
    };
}

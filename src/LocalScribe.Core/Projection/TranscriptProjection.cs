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

    private sealed record PreRow(long StartMs, int SourceRank, int Seq, string? Name, string Text, bool IsMarker);

    public IReadOnlyList<DisplayRow> Build(
        IReadOnlyList<TranscriptLine> lines, Speakers? speakers, Edits? edits, SessionMeta meta)
    {
        var matterIds = meta.MatterIds;

        // (1)-(3): partition; vocabulary pass; edits overlay (human verbatim wins).
        var projected = new List<ProjectedSegment>();
        var markers = new List<TranscriptLine>();
        foreach (var line in lines)
        {
            if (line.Kind == TranscriptKind.Marker) { markers.Add(line); continue; }
            string text = _vocab.ApplyCorrections(line.Text, matterIds);
            if (edits is not null && edits.Corrections.TryGetValue(line.Seq.ToString(), out Correction? c))
                text = c.Text;
            projected.Add(new ProjectedSegment(line, text));
        }

        // (4): dedup.
        var kept = _dedup.Filter(projected);

        // (5): name resolution -> flat pre-rows for segments and markers.
        var pre = new List<PreRow>();
        foreach (var s in kept)
            pre.Add(new PreRow(s.StartMs, Rank(s.Source), s.Seq,
                NameResolver.Resolve(s.Line, speakers, meta), s.Text, IsMarker: false));
        foreach (var m in markers)
            pre.Add(new PreRow(m.StartMs, Rank(m.Source), m.Seq, Name: null, m.Text, IsMarker: true));

        // (6): order (startMs, source rank, seq) then group consecutive same-name segments.
        pre.Sort((a, b) =>
        {
            int c = a.StartMs.CompareTo(b.StartMs);
            if (c != 0) return c;
            c = a.SourceRank.CompareTo(b.SourceRank);
            return c != 0 ? c : a.Seq.CompareTo(b.Seq);
        });

        var rows = new List<DisplayRow>();
        foreach (var p in pre)
        {
            if (p.IsMarker)
            {
                rows.Add(new DisplayRow { IsMarker = true, StartMs = p.StartMs, Text = p.Text });
                continue;
            }
            if (rows.Count > 0 && rows[^1] is { IsMarker: false } last && last.DisplayName == p.Name)
                rows[^1] = last with { Text = last.Text + " " + p.Text };
            else
                rows.Add(new DisplayRow { IsMarker = false, StartMs = p.StartMs, DisplayName = p.Name, Text = p.Text });
        }
        return rows;
    }

    private static int Rank(TranscriptSource s) => s switch
    {
        TranscriptSource.Local => 0,
        TranscriptSource.Remote => 1,
        _ => 2,   // System (markers)
    };
}

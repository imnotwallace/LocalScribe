using System.Text;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;
namespace LocalScribe.Core.Assistant;

/// <summary>Search-assisted excerpt context (design 2026-07-18 section 7.5): question terms
/// against the existing index, matching rows plus surrounding context, and a DISCLOSED
/// degradation header. NoMatches=true means the caller must refuse honestly - the model is
/// never asked to answer from an empty context.</summary>
public sealed record ExcerptQaContext(string ContextBody, string Disclosure,
    IReadOnlyList<DisplayRow> IncludedRows, int CtxTokens, bool NoMatches);

/// <summary>Builds the excerpt context when the raise ladder is exhausted (SessionQaContext
/// .NeedsExcerpts). Per-term queries (the search engine ANDs whitespace-split terms - a
/// natural-language question would AND to nothing); hits map back to projected rows by exact
/// (Seq, PartIndex), falling back to nearest StartMs within 2 s; windows are ranked by
/// distinct-matched-term count and merged within the operating budget's 80% gate. Pure over
/// the injected query/estimate seams (production: SearchIndexService.Query and
/// TokenBudget.EstimateTokens, bound in QaScopeFactory).</summary>
public static class ExcerptContextBuilder
{
    /// <summary>Spoken rows carried each side of a matching row.</summary>
    public const int NeighborRadius = 2;
    public const int MaxQueryTerms = 8;
    /// <summary>Excerpt mode stays at the operating budget (design section 7.2).</summary>
    public const int ExcerptCtxTokens = 32768;
    public const string GapMarker = "[...]";
    /// <summary>Design section 7.5: the disclosed-degradation answer header.</summary>
    public const string DisclosureText = "Answered from matching excerpts, not the full transcript.";

    public static ExcerptQaContext Build(string question, IReadOnlyList<DisplayRow> rows,
        string sessionId, Func<SearchQuery, IReadOnlyList<SearchResult>> query,
        Func<string, int> estimateTokens, Func<string, string>? stripLine = null)
    {
        stripLine ??= static s => s;
        var terms = TextDistance.Normalize(question)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).Distinct().Take(MaxQueryTerms).ToList();

        var spoken = new List<DisplayRow>();
        foreach (var row in rows) if (!row.IsMarker) spoken.Add(row);

        var hitTerms = new Dictionary<int, HashSet<string>>();
        foreach (string term in terms)
        {
            foreach (var result in query(new SearchQuery(term)))
            {
                if (!string.Equals(result.Session.SessionId, sessionId, StringComparison.Ordinal))
                    continue;
                foreach (var hit in result.Hits)
                {
                    if (hit.Seq < 0) continue;   // speaker-name hit: no spoken line to excerpt
                    int si = spoken.FindIndex(r => r.Segments.Any(
                        g => g.Seq == hit.Seq && g.PartIndex == hit.PartIndex));
                    if (si < 0) si = NearestByStart(spoken, hit.StartMs);
                    if (si < 0) continue;
                    if (!hitTerms.TryGetValue(si, out var set)) hitTerms[si] = set = [];
                    set.Add(term);
                }
            }
        }
        if (hitTerms.Count == 0)
            return new ExcerptQaContext("", DisclosureText, [], ExcerptCtxTokens, NoMatches: true);

        var ranked = hitTerms.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key).ToList();
        int budgetTokens = (int)(ExcerptCtxTokens * QaContextLadder.FitGate)
            - QaContextLadder.OutputReserveTokens;
        var included = new SortedSet<int>();
        foreach (int center in ranked)
        {
            var candidate = new SortedSet<int>(included);
            for (int i = Math.Max(0, center - NeighborRadius);
                 i <= Math.Min(spoken.Count - 1, center + NeighborRadius); i++)
                candidate.Add(i);
            // The FIRST window is always kept - a degraded-but-disclosed answer beats none.
            if (included.Count > 0 && estimateTokens(Render(spoken, candidate, stripLine)) > budgetTokens)
                continue;
            included = candidate;
        }
        return new ExcerptQaContext(Render(spoken, included, stripLine), DisclosureText,
            included.Select(i => spoken[i]).ToList(), ExcerptCtxTokens, NoMatches: false);
    }

    private static int NearestByStart(List<DisplayRow> spoken, long startMs)
    {
        int best = -1;
        long bestDelta = long.MaxValue;
        for (int i = 0; i < spoken.Count; i++)
        {
            long d = Math.Abs(spoken[i].StartMs - startMs);
            if (d < bestDelta) { bestDelta = d; best = i; }
        }
        return bestDelta <= 2000 ? best : -1;
    }

    private static string Render(List<DisplayRow> spoken, SortedSet<int> included,
        Func<string, string> stripLine)
    {
        var sb = new StringBuilder();
        int prev = -2;
        foreach (int i in included)
        {
            if (prev >= 0 && i > prev + 1) sb.Append(GapMarker).Append('\n');
            var row = spoken[i];
            sb.Append('[').Append(AssistantCitationFormat.Format(row.StartMs)).Append("] ")
              .Append(row.DisplayName ?? "Unknown speaker").Append(": ")
              .Append(stripLine(row.Text)).Append('\n');
            prev = i;
        }
        return sb.ToString();
    }
}

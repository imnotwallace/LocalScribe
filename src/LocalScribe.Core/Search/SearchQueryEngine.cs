// src/LocalScribe.Core/Search/SearchQueryEngine.cs
namespace LocalScribe.Core.Search;

/// <summary>Pure query semantics (design 2026-07-13 section 2.1): case-insensitive substring over
/// corrected text, original machine text, and speaker names (line speakers + session participants);
/// multiple words = AND across a session; ranked by hit count, then recency, then id (deterministic
/// tail). One hit per matched LINE (the earliest term occurrence chooses the snippet); a term found
/// nowhere in any line falls back to speaker names - a term satisfied by NEITHER fails the whole
/// session (AND). Matches found only in original machine text are labelled (MatchesOriginalOnly) -
/// corrections never hide content from search. No IO, no mutation.</summary>
public static class SearchQueryEngine
{
    public const int SnippetRadius = 60;

    public static IReadOnlyList<SearchResult> Run(IEnumerable<SearchSessionEntry> sessions, SearchQuery query)
    {
        string[] terms = (query.Text ?? "").Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        var results = new List<SearchResult>();
        foreach (var s in sessions)
        {
            if (!PassesFacets(s, query)) continue;
            var hits = MatchSession(s, terms);
            if (hits is not null) results.Add(new SearchResult(s, hits, hits.Count));
        }
        return results
            .OrderByDescending(r => r.HitCount)
            .ThenByDescending(r => r.Session.StartedAtUtc)
            .ThenBy(r => r.Session.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>±SnippetRadius chars around [matchIndex, matchIndex+matchLength); "…" marks
    /// truncation at either end (design 2.1: ±60 chars around the first hit in a line).</summary>
    public static string Snippet(string text, int matchIndex, int matchLength)
    {
        int start = Math.Max(0, matchIndex - SnippetRadius);
        int end = Math.Min(text.Length, matchIndex + matchLength + SnippetRadius);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }

    private static bool PassesFacets(SearchSessionEntry s, SearchQuery q)
    {
        if (q.MatterId is { } m && !s.MatterIds.Contains(m, StringComparer.Ordinal)) return false;
        if (q.FromUtc is { } from && s.StartedAtUtc < from) return false;
        if (q.ToUtc is { } to && s.StartedAtUtc >= to) return false;      // exclusive upper bound
        if (q.App is { } app && !string.Equals(s.App, app, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Null when any term is satisfied by neither line text nor a speaker name (AND).</summary>
    private static IReadOnlyList<SearchHit>? MatchSession(SearchSessionEntry s, string[] terms)
    {
        var satisfiedByText = new bool[terms.Length];
        var hits = new List<SearchHit>();

        foreach (var line in s.Lines)
        {
            // Earliest occurrence across terms picks the snippet; corrected text beats original.
            int firstTextIdx = -1, firstTextLen = 0; string firstTextTerm = "";
            int firstOrigIdx = -1, firstOrigLen = 0; string firstOrigTerm = "";
            for (int i = 0; i < terms.Length; i++)
            {
                int ti = line.Text.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase);
                if (ti >= 0)
                {
                    satisfiedByText[i] = true;
                    if (firstTextIdx < 0 || ti < firstTextIdx)
                    { firstTextIdx = ti; firstTextLen = terms[i].Length; firstTextTerm = terms[i]; }
                }
                int oi = line.OriginalText?.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) ?? -1;
                if (oi >= 0)
                {
                    satisfiedByText[i] = true;
                    if (firstOrigIdx < 0 || oi < firstOrigIdx)
                    { firstOrigIdx = oi; firstOrigLen = terms[i].Length; firstOrigTerm = terms[i]; }
                }
            }
            if (firstTextIdx >= 0)
                hits.Add(new SearchHit(line.Seq, line.PartIndex, line.StartMs, line.Speaker,
                    Snippet(line.Text, firstTextIdx, firstTextLen), firstTextTerm,
                    MatchesOriginalOnly: false, IsSpeakerNameMatch: false));
            else if (firstOrigIdx >= 0)
                hits.Add(new SearchHit(line.Seq, line.PartIndex, line.StartMs, line.Speaker,
                    Snippet(line.OriginalText!, firstOrigIdx, firstOrigLen), firstOrigTerm,
                    MatchesOriginalOnly: true, IsSpeakerNameMatch: false));
        }

        // Terms unmatched by any line fall back to speaker names (line speakers + participants).
        // "Speaker-name-only" (design 2.1): the term matched no line text at all in this session.
        // Dedup case-insensitively (the same person entered with different casing is one name -
        // consistent with this engine's case-insensitive matching everywhere else).
        var speakerNames = s.Lines.Select(l => l.Speaker)
            .Concat(s.Participants)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        for (int i = 0; i < terms.Length; i++)
        {
            if (satisfiedByText[i]) continue;
            // B4-1: ONE hit per term for the name fallback, not one per matching name - several
            // similarly-named speakers must not inflate HitCount/ranking. The first matching name
            // (ordinal order) represents the session; a null here fails the AND for this term.
            string? matchName = speakerNames.FirstOrDefault(
                n => n.IndexOf(terms[i], StringComparison.OrdinalIgnoreCase) >= 0);
            if (matchName is null) return null;               // this term matched nothing -> AND fails
            var firstLine = s.Lines.FirstOrDefault(
                l => string.Equals(l.Speaker, matchName, StringComparison.OrdinalIgnoreCase));
            hits.Add(firstLine is not null
                ? new SearchHit(firstLine.Seq, firstLine.PartIndex, firstLine.StartMs, matchName,
                    Snippet(firstLine.Text, 0, 0), terms[i],
                    MatchesOriginalOnly: false, IsSpeakerNameMatch: true)
                : new SearchHit(-1, 0, 0, matchName, "", terms[i],
                    MatchesOriginalOnly: false, IsSpeakerNameMatch: true));
        }
        return hits;
    }
}

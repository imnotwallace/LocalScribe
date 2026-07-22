using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>One tagged session's latest summary for the matter scope (design 2026-07-18
/// section 7.5): SUMMARIES only, never transcripts. Null SummaryMarkdown = no summary yet -
/// listed for generation, never silently absent. Filled by the App composition from the
/// foundation branch's SummaryStore.</summary>
public sealed record MatterSummarySource(string SessionId, string Title,
    DateTimeOffset StartedAtLocal, string? SummaryMarkdown, bool Stale);

/// <summary>Matter-scope context plus the EXPLICIT coverage lists the UI must disclose
/// (design 7.5: "the UI lists exactly which sessions are included/omitted - no silent
/// truncation").</summary>
public sealed record MatterQaContext(string ContextBody, int CtxTokens,
    IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds,
    IReadOnlyList<string> MissingSummarySessionIds);

/// <summary>Newest-first per-session summaries within the operating budget's 80% gate. The cut
/// is a strict prefix: once a summary does not fit, it and every older one are OMITTED (one
/// honest cut line beats cherry-picking). Stale summaries are included with an in-context
/// note. Pure; estimateTokens is the TokenBudget seam.</summary>
public static class MatterQaContextBuilder
{
    public const string StaleNote =
        "(This summary may be out of date - the transcript changed after it was generated.)";

    public static MatterQaContext Build(IReadOnlyList<MatterSummarySource> sessions,
        Func<string, int> estimateTokens)
    {
        var newestFirst = sessions
            .OrderByDescending(s => s.StartedAtLocal)
            .ThenByDescending(s => s.SessionId, StringComparer.Ordinal).ToList();
        var included = new List<string>();
        var omitted = new List<string>();
        var missing = new List<string>();
        int budget = (int)(32768 * QaContextLadder.FitGate) - QaContextLadder.OutputReserveTokens;
        var sb = new StringBuilder();
        bool full = false;
        foreach (var s in newestFirst)
        {
            if (string.IsNullOrWhiteSpace(s.SummaryMarkdown)) { missing.Add(s.SessionId); continue; }
            if (full) { omitted.Add(s.SessionId); continue; }
            string block = Block(s);
            if (included.Count > 0 && estimateTokens(sb.ToString() + block) > budget)
            {
                full = true;               // strict prefix: this one and everything older is out
                omitted.Add(s.SessionId);
                continue;
            }
            sb.Append(block);
            included.Add(s.SessionId);
        }
        string body = sb.ToString();
        return new MatterQaContext(body, QaContextLadder.Pick(estimateTokens(body)) ?? 32768,
            included, omitted, missing);
    }

    private static string Block(MatterSummarySource s)
        => "## " + s.Title + " ("
         + s.StartedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")\n"
         + (s.Stale ? StaleNote + "\n" : "")
         + s.SummaryMarkdown + "\n\n";
}

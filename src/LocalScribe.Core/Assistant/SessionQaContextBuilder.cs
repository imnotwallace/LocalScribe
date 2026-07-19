using System.Text;
using LocalScribe.Core.Projection;
namespace LocalScribe.Core.Assistant;

/// <summary>Per-job num_ctx sizing (design 2026-07-18 sections 7.2 + 7.4-7.5): the smallest
/// ladder step whose 80% gate holds the prompt plus a fixed output reserve. 8k/16k fit a 6 GB
/// GPU beside the weights; 32k is the operating budget; 64k is the CPU-RAM-backed raise; null
/// means even the raise cannot hold the transcript - fall to search-assisted excerpting
/// (section 7.5), never to silent truncation.</summary>
public static class QaContextLadder
{
    public static readonly IReadOnlyList<int> CtxSteps = [8192, 16384, 32768, 65536];
    /// <summary>Section 7.4: the fits-gate triggers at 80% of num_ctx - BEFORE overflow.</summary>
    public const double FitGate = 0.80;
    /// <summary>Tokens reserved for the generated answer.</summary>
    public const int OutputReserveTokens = 1024;

    public static int? Pick(int promptTokens)
    {
        foreach (int step in CtxSteps)
            if (promptTokens + OutputReserveTokens <= (int)(step * FitGate)) return step;
        return null;
    }
}

/// <summary>Session-scope Q&amp;A context (design 2026-07-18 section 7.5): the full projected
/// transcript, one anchored line per spoken row. CtxTokens is the per-job num_ctx pick; null
/// means the raise ladder is exhausted and the caller must build excerpts instead. Rows is the
/// SAME list the citation validator resolves against - anchor and ground truth cannot drift.</summary>
public sealed record SessionQaContext(string ContextBody, IReadOnlyList<string> SpeakerNames,
    IReadOnlyList<DisplayRow> Rows, int? CtxTokens)
{
    public bool NeedsExcerpts => CtxTokens is null;
}

/// <summary>Builds the session-scope prompt body: raw leading timestamps are stripped through
/// the injected seam (production: AssistantInputShaper.StripLeadingTimestamps), then the
/// CANONICAL [HH:MM:SS] anchor is injected app-side - the model can only cite anchors that
/// exist, and the validator parses the same family back (AssistantCitationFormat). Markers are
/// excluded (system notes, not speech). Pure; estimateTokens is a seam (production:
/// TokenBudget.EstimateTokens) so tests stay deterministic.</summary>
public static class SessionQaContextBuilder
{
    public static SessionQaContext Build(IReadOnlyList<DisplayRow> rows,
        Func<string, int> estimateTokens, Func<string, string>? stripLine = null)
    {
        stripLine ??= static s => s;
        var sb = new StringBuilder();
        var names = new List<string>();
        foreach (var row in rows)
        {
            if (row.IsMarker) continue;
            string name = row.DisplayName ?? "Unknown speaker";
            if (!names.Contains(name)) names.Add(name);
            sb.Append('[').Append(AssistantCitationFormat.Format(row.StartMs)).Append("] ")
              .Append(name).Append(": ").Append(stripLine(row.Text)).Append('\n');
        }
        string body = sb.ToString();
        return new SessionQaContext(body, names, rows, QaContextLadder.Pick(estimateTokens(body)));
    }
}

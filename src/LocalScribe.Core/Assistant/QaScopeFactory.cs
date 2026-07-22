using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;
namespace LocalScribe.Core.Assistant;

/// <summary>Everything one ask needs (design 2026-07-18 section 7.5): the warmup request (the
/// scope context, prefilled once into the warm helper) plus the validation ground truth
/// (SessionRows for session scope, MatterSummaries for matter scope) and the explicit coverage
/// lists. NoMatches=true means refuse honestly before the model is engaged. SpeakerPreamble +
/// ContextText are the exact prompt ingredients used to build the warmup (ContextText already
/// carries the excerpt-mode disclosure when applicable) so AssistantQaService can rebuild the
/// FULL per-question prompt each ask, byte-identical up to the question tail, so the helper's KV
/// prefix reuse actually engages (contract resolution #4).</summary>
public sealed record QaScope(AssistantRequest WarmupRequest, string Model, string PromptVersion,
    bool ExcerptMode, string? Disclosure, bool NoMatches,
    string? SessionId, IReadOnlyList<DisplayRow>? SessionRows,
    IReadOnlyList<MatterSummarySource>? MatterSummaries,
    IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds,
    IReadOnlyList<string> MissingSummarySessionIds,
    string SpeakerPreamble, string ContextText);

/// <summary>Builds QaScopes. THE ONLY Core file of this branch that calls foundation
/// prompt/budget/shaper members - every call site is CONTRACT-marked; re-verify the real
/// signatures on the merged master and adapt identifiers only, never behavior.</summary>
public sealed class QaScopeFactory
{
    /// <summary>Tiny output cap for the warmup ask - it exists only to load the model and
    /// prefill the shared prompt prefix, never to produce a real answer (contract resolution #4).</summary>
    public const int WarmupMaxTokens = 16;
    /// <summary>Output cap for a real per-question answer.</summary>
    public const int MaxAnswerTokens = 1024;

    private readonly string _modelPath;
    private readonly string _modelFile;
    private readonly string _requestedBackend;
    private readonly Func<SearchQuery, IReadOnlyList<SearchResult>> _search;

    public QaScopeFactory(string modelPath, string modelFileName, string requestedBackend,
        Func<SearchQuery, IReadOnlyList<SearchResult>> search)
        => (_modelPath, _modelFile, _requestedBackend, _search)
            = (modelPath, modelFileName, requestedBackend, search);

    /// <summary>Session scope: full projected transcript with the raise ladder; excerpts only
    /// when even 64k cannot hold it (design 7.5 raise-or-excerpt, disclosed).</summary>
    public async Task<QaScope> ForSessionAsync(string sessionId,
        Func<CancellationToken, Task<IReadOnlyList<DisplayRow>>> loadRows,
        string question, CancellationToken ct)
    {
        IReadOnlyList<DisplayRow> rows = await loadRows(ct);
        // CONTRACT (resolved): TokenBudget.EstimateTokens(int chars) - bind the char-count seam;
        // AssistantInputShaper.StripLeadingTimestamps(string) - verified verbatim.
        var full = SessionQaContextBuilder.Build(rows, s => TokenBudget.EstimateTokens(s.Length),
            AssistantInputShaper.StripLeadingTimestamps);
        string body;
        int ctx;
        bool excerptMode = false;
        string? disclosure = null;
        bool noMatches = false;
        if (!full.NeedsExcerpts)
        {
            body = full.ContextBody;
            ctx = full.CtxTokens!.Value;
        }
        else
        {
            var ex = ExcerptContextBuilder.Build(question, rows, sessionId, _search,
                s => TokenBudget.EstimateTokens(s.Length), AssistantInputShaper.StripLeadingTimestamps);
            (body, ctx, excerptMode, disclosure, noMatches)
                = (ex.ContextBody, ex.CtxTokens, true, ex.Disclosure, ex.NoMatches);
        }
        // CONTRACT (resolved): BuildSpeakerPreamble over the display-name roster.
        string preamble = AssistantInputShaper.BuildSpeakerPreamble(full.SpeakerNames);
        // Contract resolution #3: the excerpt-mode disclosure travels INSIDE contextText (never
        // as a separate prompt slot) - prepended once here so both the warmup prefill and every
        // per-question ask see it as part of the same prefilled context.
        string contextText = excerptMode && disclosure is not null ? disclosure + "\n\n" + body : body;
        var warmup = Warmup(preamble, contextText, ctx);
        return new QaScope(warmup, _modelFile, $"{AssistantPrompts.PromptVersion}", excerptMode, disclosure,
            noMatches, sessionId, rows, null, [sessionId], [], [], preamble, contextText);
    }

    /// <summary>Matter scope: newest-first summaries within budget, explicit coverage.</summary>
    public Task<QaScope> ForMatterAsync(IReadOnlyList<MatterSummarySource> sessions, CancellationToken ct)
    {
        // CONTRACT (resolved): TokenBudget.EstimateTokens(int chars).
        var mc = MatterQaContextBuilder.Build(sessions, s => TokenBudget.EstimateTokens(s.Length));
        var includedSources = sessions.Where(s => mc.IncludedSessionIds.Contains(s.SessionId)).ToList();
        string? disclosure = mc.OmittedSessionIds.Count + mc.MissingSummarySessionIds.Count > 0
            ? "Answered from per-session summaries: " + mc.IncludedSessionIds.Count + " of "
              + sessions.Count + " tagged sessions included."
            : null;
        // Matter scope has no speaker preamble (design 7.5); the matter disclosure above is
        // UI-only presentation, never prepended into the model's context.
        string contextText = mc.ContextBody;
        var warmup = Warmup("", contextText, mc.CtxTokens);
        return Task.FromResult(new QaScope(warmup, _modelFile, $"{AssistantPrompts.PromptVersion}", false,
            disclosure, mc.IncludedSessionIds.Count == 0, null, null, includedSources,
            mc.IncludedSessionIds, mc.OmittedSessionIds, mc.MissingSummarySessionIds, "", contextText));
    }

    /// <summary>Warmup ask: the FULL answer prompt with an EMPTY question, tiny output cap
    /// (contract resolution #4 - one v1 payload shape via AssistantWire.PromptPayload for both
    /// the warmup and every real ask; only the question tail differs between them, so the
    /// helper's KV prefix reuse actually engages). KeepAlive is true - the warm-helper contract
    /// (design 7.1).</summary>
    private AssistantRequest Warmup(string speakerPreamble, string contextText, int ctxTokens)
        => new(Op: "answer", ModelPath: _modelPath, CtxTokens: ctxTokens,
               Backend: _requestedBackend, KeepAlive: true,
               PayloadJson: AssistantWire.PromptPayload(
                   AssistantPrompts.BuildAnswerPrompt(speakerPreamble, contextText, ""), WarmupMaxTokens));
}

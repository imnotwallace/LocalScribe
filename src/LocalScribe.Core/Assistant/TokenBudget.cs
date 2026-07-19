namespace LocalScribe.Core.Assistant;

/// <summary>Fits-check arithmetic (design 2026-07-18 section 7.4, LOCKED contract - feat/matter-qa
/// consumes EstimateTokens/NeedsChunking/ChunkBudgetChars for its raise-or-excerpt policy).
/// Worst-case 2 chars/token so the gate always trips BEFORE overflow, never after.</summary>
public static class TokenBudget
{
    public const int WorstCaseCharsPerToken = 2;
    public const int FitsGatePercent = 80;
    public const int MapOutputCapTokens = 600;
    public const int MaxReduceDepth = 2;
    /// <summary>Output tokens reserved when sizing a job's ctx (summary body budget).</summary>
    public const int OutputReserveTokens = 1200;
    public const int MinCtxTokens = 4096;
    /// <summary>The 32k operating budget (design decisions log: context budget).</summary>
    public const int MaxCtxTokens = 32768;

    public static int EstimateTokens(int chars)
        => (chars + WorstCaseCharsPerToken - 1) / WorstCaseCharsPerToken;

    public static bool NeedsChunking(int estimatedTokens, int ctxTokens)
        => estimatedTokens > ctxTokens * FitsGatePercent / 100;

    /// <summary>Input chars a map chunk may carry inside ctxTokens, after reserving the
    /// map output cap - so chunk input + chunk output both fit under the 80% gate.</summary>
    public static int ChunkBudgetChars(int ctxTokens)
        => (ctxTokens * FitsGatePercent / 100 - MapOutputCapTokens) * WorstCaseCharsPerToken;

    /// <summary>Per-job num_ctx (design 7.2: sized to the job, not a fixed max): input estimate
    /// plus the output reserve, grossed up so the 80% gate passes, clamped to the operating
    /// budget. Beyond MaxCtxTokens the caller goes to map-reduce instead.</summary>
    public static int JobCtxTokens(int estimatedInputTokens)
        => Math.Clamp((estimatedInputTokens + OutputReserveTokens) * 100 / FitsGatePercent + 1,
            MinCtxTokens, MaxCtxTokens);
}

namespace LocalScribe.Core.Assistant;

/// <summary>Matter-scope citation post-validation (design 2026-07-18 section 7.5): the context
/// is summaries, not transcripts, so a claim verifies when its cited time appears as a stamp
/// inside an included summary AND the claim fuzzy-matches that summary's text (same TextDistance
/// thresholds as the session validator). Chips navigate (open the session's Read view, no
/// scroll - Seq stays -1) only when the cited time lives in exactly ONE session's summary; a
/// bare HH:MM:SS is ambiguous across sessions (recorded v1 constraint). Unverifiable claims
/// are FLAGGED, never dropped.</summary>
public static class MatterCitationValidator
{
    public static ValidatedAnswer Validate(string answerMarkdown,
        IReadOnlyList<MatterSummarySource> includedSummaries)
    {
        var summaryStamps = includedSummaries
            .Where(s => !string.IsNullOrWhiteSpace(s.SummaryMarkdown))
            .Select(s => (Source: s, Stamps: AssistantCitationFormat.StampsIn(s.SummaryMarkdown!)))
            .ToList();
        var lines = new List<AnswerLine>();
        int unverifiable = 0;
        foreach (var part in AssistantCitationFormat.SplitAnswer(answerMarkdown))
        {
            // Cross-task seam (same fix as CitationValidator, Task 2): SplitAnswer marks a
            // '#'-prefixed line as IsClaim=false purely on the markdown-header rule, EVEN IF it
            // carries a valid stamp (Stamps is still populated for such a line). Gating
            // validation on IsClaim alone would let a factual claim hidden behind a header
            // prefix bypass matter-scope citation checking entirely - so any STAMP-BEARING line
            // is validated here, not just claims. A genuine header/lead-in with no stamp
            // (Stamps.Count == 0) still skips untouched.
            bool shouldValidate = part.IsClaim || part.Stamps.Count > 0;
            if (!shouldValidate)
            {
                lines.Add(new AnswerLine(part.RawLine, [], false, false, null));
                continue;
            }
            var chips = new List<CitationChip>();
            bool anyStampFound = false, anyVerified = false;
            foreach (var stamp in part.Stamps)
            {
                var carriers = summaryStamps.Where(x => x.Stamps.Any(t => t.Ms == stamp.Ms)).ToList();
                if (carriers.Count == 0)
                {
                    chips.Add(new CitationChip(stamp.Token, false, null, -1, ""));
                    continue;
                }
                anyStampFound = true;
                var matching = carriers.Where(c => CitationValidator.ClaimScore(
                        part.ClaimText, c.Source.SummaryMarkdown!) >= CitationValidator.MatchThreshold)
                    .ToList();
                bool ok = matching.Count > 0;
                anyVerified |= ok;
                string? sessionId = ok && matching.Count == 1 ? matching[0].Source.SessionId : null;
                chips.Add(new CitationChip(stamp.Token, ok, sessionId, -1, ""));
            }
            string? reason = anyVerified ? null
                : part.Stamps.Count == 0 ? "no citation"
                : !anyStampFound ? "cited time not found in the included summaries"
                : "text does not match the cited summary";
            if (reason is not null) unverifiable++;
            // IsClaim reflects SplitAnswer's own classification verbatim (never faked true here) -
            // only the DECISION to validate widens for stamp-bearing lines, not this reported flag.
            lines.Add(new AnswerLine(part.ClaimText, chips, part.IsClaim, reason is not null, reason));
        }
        return new ValidatedAnswer(lines, unverifiable);
    }
}

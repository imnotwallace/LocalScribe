using LocalScribe.Core.Projection;
namespace LocalScribe.Core.Assistant;

/// <summary>One rendered citation pill of an assistant answer (design 2026-07-18 section 7.5).
/// Verified chips click through to the Read view (SessionId + Seq + a find-highlight NavTerm);
/// unverified chips render visibly "unresolved" and never navigate. Serialized into chats.json
/// as part of the turn so history renders exactly what was shown when answered.</summary>
public sealed record CitationChip(string Stamp, bool Verified, string? SessionId, int Seq, string NavTerm);

/// <summary>One rendered answer line. Unverifiable claims are FLAGGED (the "uncited" chip),
/// never dropped or rewritten - the locked section 7.5 rule; Text is always the full claim.</summary>
public sealed record AnswerLine(string Text, IReadOnlyList<CitationChip> Chips,
    bool IsClaim, bool Unverifiable, string? Reason);

/// <summary>A validated answer: presentation-ready lines plus the flagged-claim count.</summary>
public sealed record ValidatedAnswer(IReadOnlyList<AnswerLine> Lines, int UnverifiableCount);

/// <summary>Session-scope citation post-validation (design 2026-07-18 section 7.5): each cited
/// [HH:MM:SS] must resolve to a real projected row (2 s tolerance on the row start, or inside
/// the row's span) AND the claim text must fuzzy-match that row's text via TextDistance. Pure -
/// the ground truth is the SAME LoadedProjection.Rows every renderer consumes.</summary>
public static class CitationValidator
{
    /// <summary>Design section 7.5: plus/minus 2 s resolution tolerance.</summary>
    public const long ToleranceMs = 2000;

    /// <summary>Fuzzy floor for both scoring paths. ContainmentSimilarity handles claims at or
    /// above its own 12-char/3-token floor (extractive claim inside a longer turn ~0.8+,
    /// unrelated text ~0.2-0.3); NormalizedSimilarity covers short replies where containment
    /// returns 0 by design. A wrong verdict only mis-flags VISIBLY (never hides content), so
    /// this constant is not golden-corpus-gated like the dedup floors.</summary>
    public const double MatchThreshold = 0.60;

    public static ValidatedAnswer Validate(string answerMarkdown, IReadOnlyList<DisplayRow> rows,
        string sessionId)
    {
        var lines = new List<AnswerLine>();
        int unverifiable = 0;
        foreach (var part in AssistantCitationFormat.SplitAnswer(answerMarkdown))
        {
            // Cross-task seam (Task 1 review): SplitAnswer marks a '#'-prefixed line as
            // IsClaim=false purely on the markdown-header rule, EVEN IF it carries a valid
            // stamp (Stamps is still populated for such a line). Gating validation on IsClaim
            // alone would let a factual claim hidden behind a header prefix bypass citation
            // checking entirely - so any STAMP-BEARING line is validated here, not just claims.
            // A genuine header/lead-in with no stamp (Stamps.Count == 0) still skips untouched.
            bool shouldValidate = part.IsClaim || part.Stamps.Count > 0;
            if (!shouldValidate)
            {
                lines.Add(new AnswerLine(part.RawLine, [], false, false, null));
                continue;
            }
            var chips = new List<CitationChip>();
            bool anyResolved = false, anyVerified = false;
            foreach (var stamp in part.Stamps)
            {
                DisplayRow? best = null;
                double bestScore = -1;
                foreach (var row in rows)
                {
                    // A non-marker row with EMPTY Segments cannot yield a real Seq for
                    // click-through (Segments[0].Seq would fall back to -1) - the chip
                    // invariant "Verified=false => SessionId=null, Seq=-1" also means a
                    // citation that cannot be made clickable must never be falsely verified.
                    // Treat it as non-resolvable, same as a marker (defense-in-depth: the
                    // loader always populates Segments in practice).
                    if (row.IsMarker || row.Segments.Count == 0) continue;
                    bool near = Math.Abs(row.StartMs - stamp.Ms) <= ToleranceMs
                        || (stamp.Ms >= row.StartMs && stamp.Ms <= row.EndMs);
                    if (!near) continue;
                    double score = ClaimScore(part.ClaimText, row.Text);
                    if (score > bestScore) { bestScore = score; best = row; }
                }
                if (best is null)
                {
                    chips.Add(new CitationChip(stamp.Token, false, null, -1, ""));
                    continue;
                }
                anyResolved = true;
                bool ok = bestScore >= MatchThreshold;
                anyVerified |= ok;
                int seq = best.Segments.Count > 0 ? best.Segments[0].Seq : -1;
                chips.Add(ok
                    ? new CitationChip(stamp.Token, true, sessionId, seq, NavTerm(part.ClaimText, best.Text))
                    : new CitationChip(stamp.Token, false, null, -1, ""));
            }
            string? reason = anyVerified ? null
                : part.Stamps.Count == 0 ? "no citation"
                : !anyResolved ? "cited time not found in the record"
                : "text does not match the cited segment";
            if (reason is not null) unverifiable++;
            // IsClaim reflects SplitAnswer's own classification verbatim (never faked true here) -
            // only the DECISION to validate widens for stamp-bearing lines, not this reported flag.
            lines.Add(new AnswerLine(part.ClaimText, chips, part.IsClaim, reason is not null, reason));
        }
        return new ValidatedAnswer(lines, unverifiable);
    }

    /// <summary>Best of containment (claim-inside-turn) and whole-string similarity (short
    /// replies below containment's internal floor).</summary>
    public static double ClaimScore(string claim, string segmentText)
        => Math.Max(TextDistance.ContainmentSimilarity(claim, segmentText),
                    TextDistance.NormalizedSimilarity(claim, segmentText));

    /// <summary>The find-bar highlight term for click-through: the longest normalized claim
    /// word (4+ chars) that actually appears in the matched row's text; "" when none does
    /// (ShowFindAt still scrolls to the row - the term only drives highlighting).</summary>
    public static string NavTerm(string claim, string rowText)
    {
        string best = "";
        foreach (string w in TextDistance.Normalize(claim).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w.Length >= 4 && w.Length > best.Length
                && rowText.Contains(w, StringComparison.OrdinalIgnoreCase))
                best = w;
        return best;
    }
}

using System.Text;

namespace LocalScribe.Core.Assistant;

/// <summary>Every prompt the assistant sends (design 2026-07-18 sections 7.4/7.5). LOCKED
/// contract: PromptVersion is a bumped constant covering EVERY prompt change (artifacts are
/// reproducible-in-principle); BuildAnswerPrompt is produced here and consumed by
/// feat/matter-qa. Snapshot tests pin all output - changing any text here without bumping
/// PromptVersion is a blocking defect.</summary>
public static class AssistantPrompts
{
    public const int PromptVersion = 1;

    /// <summary>The locked artifact label (design section 1, evidentiary rules). Em dash
    /// escaped so this source file stays ASCII.</summary>
    public const string DraftLabel = "AI-generated draft \u2014 not a transcript; verify against the record.";

    /// <summary>User-invisible grounding line, always appended app-side (design 7.4).</summary>
    public const string GroundingLine =
        "Extract only what is explicitly stated in the transcript. Do not infer, speculate, or add outside knowledge.";

    /// <summary>The four fixed English section headers (body language follows the session).</summary>
    public static readonly IReadOnlyList<string> SectionHeaders =
        ["Summary", "Key topics", "Key statements", "Follow-ups & commitments"];

    private static string SectionHeaderBlock()
        => string.Join('\n', SectionHeaders.Select(h => "## " + h));

    public static string BuildSummaryPrompt(string speakerPreamble, string transcriptText)
        => "You are producing a private recall aid from a call transcript.\n"
         + (speakerPreamble.Length > 0 ? speakerPreamble + "\n" : "")
         + "Write exactly these Markdown sections, in this order, using these exact headers:\n"
         + SectionHeaderBlock() + "\n"
         + GroundingLine + "\n"
         + "If a section has nothing explicitly stated, write: None stated.\n"
         + "Transcript:\n"
         + transcriptText;

    public static string BuildMapPrompt(string speakerPreamble, string chunkText, int chunkIndex, int chunkCount)
        => $"You are reading part {chunkIndex} of {chunkCount} of a call transcript.\n"
         + (speakerPreamble.Length > 0 ? speakerPreamble + "\n" : "")
         + "List this part's topics, key statements (with who said them), and any follow-ups or "
         + $"commitments, as terse bullet notes of at most {TokenBudget.MapOutputCapTokens} tokens.\n"
         + GroundingLine + "\n"
         + "Transcript part:\n"
         + chunkText;

    public static string BuildReducePrompt(string speakerPreamble, IReadOnlyList<string> mapOutputs)
    {
        var sb = new StringBuilder()
            .Append("You are merging per-part notes from one call into a single recall aid.\n");
        if (speakerPreamble.Length > 0) sb.Append(speakerPreamble).Append('\n');
        sb.Append("Write exactly these Markdown sections, in this order, using these exact headers:\n")
          .Append(SectionHeaderBlock()).Append('\n')
          .Append(GroundingLine).Append('\n')
          .Append("If a section has nothing explicitly stated, write: None stated.\n");
        for (int i = 0; i < mapOutputs.Count; i++)
            sb.Append($"Part {i + 1} notes:\n").Append(mapOutputs[i]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Strict-extractive Q&A (design 7.5): inference FORBIDDEN, one [HH:MM:SS]
    /// citation per claim. Consumed by feat/matter-qa; pinned here so it cannot drift.</summary>
    public static string BuildAnswerPrompt(string speakerPreamble, string contextText, string question)
        => "Answer the question using ONLY the context below.\n"
         + GroundingLine + "\n"
         + "Every claim in your answer must cite the timestamp of the segment it comes from, "
         + "in the form [HH:MM:SS], immediately after the claim.\n"
         + "If the context does not explicitly answer the question, say exactly that.\n"
         + (speakerPreamble.Length > 0 ? speakerPreamble + "\n" : "")
         + "Context:\n" + contextText + "\n"
         + "Question:\n" + question;
}

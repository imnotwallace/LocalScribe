using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>Renders a chat thread's memory (design 2026-07-24) as the block inserted BETWEEN the
/// scope context and the new question in the answer prompt: the running recap (condensed oldest
/// turns) then the verbatim prior turns as labelled Q/A pairs. Pure and snapshot-adjacent - the
/// answer prompt's PromptVersion covers it. Empty when a thread has no history yet, so a first
/// question reduces to today's single-turn tail.</summary>
public static class AssistantConversation
{
    public static string BuildHistoryBlock(string? recap, IReadOnlyList<AssistantChatTurn> priorTurns)
    {
        if (string.IsNullOrEmpty(recap) && priorTurns.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("Earlier in this conversation (for reference; still cite the transcript):\n");
        if (!string.IsNullOrEmpty(recap))
            sb.Append("Summary of earlier exchanges: ").Append(recap).Append('\n');
        foreach (var t in priorTurns)
            sb.Append("Q: ").Append(t.Question).Append('\n')
              .Append("A: ").Append(t.AnswerMarkdown).Append('\n');
        return sb.ToString();
    }
}

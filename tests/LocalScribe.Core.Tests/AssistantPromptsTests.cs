using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantPromptsTests
{
    // SNAPSHOT TESTS keyed to PromptVersion (design 2026-07-18 section 8). If any assertion
    // here needs changing, PromptVersion MUST be bumped in the same commit - that is the point.

    [Fact]
    public void Prompt_version_is_two_and_the_locked_constants_hold()
    {
        Assert.Equal(2, AssistantPrompts.PromptVersion);   // bumped 1 -> 2: threaded answer prompt (design 2026-07-24)
        Assert.Equal("AI-generated draft \u2014 not a transcript; verify against the record.",
            AssistantPrompts.DraftLabel);   // em dash as escape: test source stays ASCII (project rule)
        Assert.Equal(new[] { "Summary", "Key topics", "Key statements", "Follow-ups & commitments" },
            AssistantPrompts.SectionHeaders);
        Assert.Contains("only what is explicitly stated", AssistantPrompts.GroundingLine);
        Assert.Contains("Do not infer", AssistantPrompts.GroundingLine);
    }

    [Fact]
    public void Summary_prompt_snapshot()
    {
        string p = AssistantPrompts.BuildSummaryPrompt("Speakers in this call: Sam.", "Sam: Hi.");
        Assert.Equal(
            "You are producing a private recall aid from a call transcript.\n" +
            "Speakers in this call: Sam.\n" +
            "Write exactly these Markdown sections, in this order, using these exact headers:\n" +
            "## Summary\n## Key topics\n## Key statements\n## Follow-ups & commitments\n" +
            AssistantPrompts.GroundingLine + "\n" +
            "If a section has nothing explicitly stated, write: None stated.\n" +
            "Transcript:\n" +
            "Sam: Hi.", p);
    }

    [Fact]
    public void Map_prompt_caps_output_and_names_the_part()
    {
        string p = AssistantPrompts.BuildMapPrompt("", "Sam: Hi.", 2, 5);
        Assert.Contains("part 2 of 5", p);
        Assert.Contains($"at most {TokenBudget.MapOutputCapTokens} tokens", p);
        Assert.Contains(AssistantPrompts.GroundingLine, p);
        Assert.EndsWith("Sam: Hi.", p);
    }

    [Fact]
    public void Reduce_prompt_merges_numbered_part_notes_into_the_sections()
    {
        string p = AssistantPrompts.BuildReducePrompt("Speakers in this call: Sam.", ["notes A", "notes B"]);
        Assert.Contains("## Summary", p);
        Assert.Contains("## Follow-ups & commitments", p);
        Assert.Contains("Part 1 notes:\nnotes A", p);
        Assert.Contains("Part 2 notes:\nnotes B", p);
        Assert.Contains(AssistantPrompts.GroundingLine, p);
    }

    [Fact]
    public void Answer_prompt_is_strict_extractive_with_timestamp_citations()
    {
        // Produced HERE, consumed by feat/matter-qa (design 7.5) - pinned so it cannot drift.
        string p = AssistantPrompts.BuildAnswerPrompt("", "ctx", "", "What was agreed?");
        Assert.Contains("ONLY the context below", p);
        Assert.Contains("[HH:MM:SS]", p);
        Assert.Contains("does not explicitly answer", p);
        Assert.Contains(AssistantPrompts.GroundingLine, p);
        Assert.Contains("Question:\nWhat was agreed?", p);
    }

    // design 2026-07-24: a first question's empty historyBlock reduces the prompt to today's
    // exact single-turn tail (PromptVersion 2's new shape is a pure ADDITION, never a rewrite of
    // the pre-threading text) - the empty-history case above pins that. This pins the non-empty
    // case: historyBlock sits strictly BETWEEN the context and the question.
    [Fact]
    public void Non_empty_history_block_sits_between_context_and_question()
    {
        string p = AssistantPrompts.BuildAnswerPrompt("", "ctx", "Earlier: Q: x\nA: y\n", "What was agreed?");
        int contextIdx = p.IndexOf("Context:\nctx", StringComparison.Ordinal);
        int historyIdx = p.IndexOf("Earlier: Q: x\nA: y\n", StringComparison.Ordinal);
        int questionIdx = p.IndexOf("Question:\nWhat was agreed?", StringComparison.Ordinal);
        Assert.True(contextIdx >= 0 && historyIdx > contextIdx && questionIdx > historyIdx);
    }

    [Fact]
    public void Recap_prompt_carries_the_prior_recap_and_the_oldest_turn_without_new_knowledge()
    {
        var oldest = new AssistantChatTurn("t1", DateTimeOffset.UtcNow, "What was agreed?",
            "Ten thousand dollars [00:01:05]", [], "m.gguf", "cpu", "2", false, null, ["s1"], [], [], 0);

        string withoutRecap = AssistantPrompts.BuildRecapPrompt(null, oldest);
        Assert.Contains("Condense the earlier Q&A", withoutRecap);
        Assert.Contains("Do not add anything new", withoutRecap);
        Assert.Contains("Q: What was agreed?", withoutRecap);
        Assert.Contains("A: Ten thousand dollars [00:01:05]", withoutRecap);
        Assert.DoesNotContain("Recap so far:", withoutRecap);

        string withRecap = AssistantPrompts.BuildRecapPrompt("Prior context.", oldest);
        Assert.Contains("Recap so far: Prior context.", withRecap);
    }
}

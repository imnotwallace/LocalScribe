using LocalScribe.Core.Assistant;
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Tests;

public class TokenBudgetTests
{
    [Fact]
    public void Estimate_uses_worst_case_two_chars_per_token()
    {
        // Design 2026-07-18 section 7.4: Steno's arithmetic - the gate must trip BEFORE overflow.
        Assert.Equal(0, TokenBudget.EstimateTokens(0));
        Assert.Equal(1, TokenBudget.EstimateTokens(1));    // ceil(1/2)
        Assert.Equal(1, TokenBudget.EstimateTokens(2));
        Assert.Equal(5000, TokenBudget.EstimateTokens(10000));
    }

    [Fact]
    public void Chunking_gate_trips_at_eighty_percent_of_ctx()
    {
        Assert.False(TokenBudget.NeedsChunking(12800, 16000));  // exactly 80% -> still fits
        Assert.True(TokenBudget.NeedsChunking(12801, 16000));   // one past the gate -> chunk
    }

    [Fact]
    public void Chunk_budget_reserves_the_map_output_cap()
    {
        // 80% of ctx minus the 600-token map output cap, back to chars at 2 chars/token.
        Assert.Equal((16000 * 80 / 100 - 600) * 2, TokenBudget.ChunkBudgetChars(16000));
    }

    [Fact]
    public void Job_ctx_sizes_to_the_job_within_the_operating_budget()
    {
        // Small job: floor. Mid job: input + reserve grossed up past the 80% gate. Huge: 32k cap.
        Assert.Equal(TokenBudget.MinCtxTokens, TokenBudget.JobCtxTokens(100));
        int mid = TokenBudget.JobCtxTokens(10000);
        Assert.False(TokenBudget.NeedsChunking(10000 + TokenBudget.OutputReserveTokens, mid));
        Assert.Equal(TokenBudget.MaxCtxTokens, TokenBudget.JobCtxTokens(1_000_000));
    }

    [Fact]
    public void StripLeadingTimestamps_is_line_anchored()
    {
        // Design 7.4: leading per-line timestamps stripped (a UI concern only); timestamps
        // INSIDE the utterance are content and must survive.
        Assert.Equal("hello there\nyes\n",
            AssistantInputShaper.StripLeadingTimestamps("[00:01:02] hello there\n12:34 yes\n"));
        Assert.Equal("meet at 10:30 tomorrow",
            AssistantInputShaper.StripLeadingTimestamps("meet at 10:30 tomorrow"));
        Assert.Equal("a\nb", AssistantInputShaper.StripLeadingTimestamps("[0:01:02.500] a\n01:02, b"));
    }

    [Fact]
    public void Speaker_preamble_lists_the_roster()
    {
        Assert.Equal("", AssistantInputShaper.BuildSpeakerPreamble([]));
        Assert.Equal("Speakers in this call: Sam, Client A.",
            AssistantInputShaper.BuildSpeakerPreamble(["Sam", "Client A"]));
    }

    [Fact]
    public void Transcript_text_keeps_named_speakers_and_skips_markers()
    {
        var rows = new List<DisplayRow>
        {
            new() { DisplayName = "Sam", Text = "Hello there.", StartMs = 0, EndMs = 900 },
            new() { IsMarker = true, Text = "recording paused", StartMs = 1000, EndMs = 1000 },
            new() { DisplayName = null, Text = "Hi.", StartMs = 2000, EndMs = 2500 },
        };
        Assert.Equal("Sam: Hello there.\nUnknown speaker: Hi.",
            AssistantInputShaper.BuildTranscriptText(rows));
    }
}

using LocalScribe.Core.Assistant;

public class AssistantCitationFormatTests
{
    [Fact]
    public void Format_is_zero_padded_HHMMSS()
    {
        // Design 2026-07-18 section 7.5: the canonical citation anchor. Always 2-digit hours so
        // the model sees ONE shape and the validator round-trips exactly.
        Assert.Equal("00:00:00", AssistantCitationFormat.Format(0));
        Assert.Equal("00:01:05", AssistantCitationFormat.Format(65_000));
        Assert.Equal("01:02:03", AssistantCitationFormat.Format(3_723_000));
        Assert.Equal("00:00:59", AssistantCitationFormat.Format(59_999));   // truncates, never rounds up
    }

    [Theory]
    [InlineData("00:01:05", 65_000L)]
    [InlineData("1:02:03", 3_723_000L)]    // 1-digit hours accepted
    [InlineData("12:34", 754_000L)]        // MM:SS accepted (the model may shorten)
    [InlineData("2:03", 123_000L)]
    public void TryParseMs_accepts_the_stamp_family(string token, long expected)
    {
        Assert.True(AssistantCitationFormat.TryParseMs(token, out long ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("12:99")]      // seconds out of range
    [InlineData("1:60:00")]    // minutes out of range
    [InlineData("123:00:00")]  // hours cap
    [InlineData("12")]
    [InlineData("a:bc")]
    [InlineData("")]
    public void TryParseMs_rejects_malformed_tokens(string token)
        => Assert.False(AssistantCitationFormat.TryParseMs(token, out _));

    [Fact]
    public void StampsIn_finds_every_valid_bracketed_stamp()
    {
        var stamps = AssistantCitationFormat.StampsIn(
            "He agreed [00:01:05] and later [1:02:03] confirmed; [12:99] is not a stamp.");
        Assert.Equal(2, stamps.Count);
        Assert.Equal(("00:01:05", 65_000L), (stamps[0].Token, stamps[0].Ms));
        Assert.Equal(("1:02:03", 3_723_000L), (stamps[1].Token, stamps[1].Ms));
    }

    [Fact]
    public void SplitAnswer_extracts_claims_with_their_stamps()
    {
        var parts = AssistantCitationFormat.SplitAnswer(
            "# Answer\n" +
            "Key statements:\n" +
            "- The parties agreed to settle for ten thousand dollars [00:01:05]\n" +
            "2. Payment is due Friday [00:02:10] [00:02:15]\n" +
            "\n" +
            "[00:03:00]");
        Assert.Equal(6, parts.Count);
        Assert.False(parts[0].IsClaim);                          // header
        Assert.False(parts[1].IsClaim);                          // section lead-in (trailing colon)
        Assert.True(parts[2].IsClaim);
        Assert.Equal("The parties agreed to settle for ten thousand dollars", parts[2].ClaimText);
        Assert.Equal(new[] { "00:01:05" }, parts[2].Stamps.Select(s => s.Token));
        Assert.True(parts[3].IsClaim);
        Assert.Equal("Payment is due Friday", parts[3].ClaimText);   // list marker + BOTH stamps stripped
        Assert.Equal(2, parts[3].Stamps.Count);
        Assert.False(parts[4].IsClaim);                          // blank
        Assert.False(parts[5].IsClaim);                          // stamp-only line has no claim text
        Assert.Single(parts[5].Stamps);
    }

    [Fact]
    public void SplitAnswer_leaves_invalid_stamp_shapes_in_the_text()
    {
        var parts = AssistantCitationFormat.SplitAnswer("The score was [12:99] in the match [00:01:05]");
        Assert.True(parts[0].IsClaim);
        Assert.Equal("The score was [12:99] in the match", parts[0].ClaimText);
        Assert.Single(parts[0].Stamps);
    }
}

using LocalScribe.Core.Projection;

public class TimestampParserTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 6, 30, 14, 32, 0, TimeSpan.FromHours(8));   // fixed offset -> deterministic

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(59000)]
    [InlineData(60000)]
    [InlineData(85000)]
    [InlineData(3599000)]
    [InlineData(3600000)]
    [InlineData(3903000)]
    [InlineData(7322000)]
    public void Round_trips_TimestampFormat_Stamp_in_both_modes(long ms)
    {
        // Stamp truncates to whole seconds, so whole-second inputs must survive EXACTLY.
        foreach (string mode in new[] { "relative", "wallclock" })
        {
            string stamp = TimestampFormat.Stamp(ms, mode, Started);
            Assert.True(TimestampParser.TryParse(stamp, mode, Started, out long parsed));
            Assert.Equal(ms, parsed);
        }
    }

    [Theory]
    [InlineData("00:01", 1000)]
    [InlineData("0:01", 1000)]          // single-digit minutes accepted (m:ss)
    [InlineData("01:25", 85000)]
    [InlineData("90:00", 5400000)]      // >59 minutes without an hours field is legal input
    [InlineData("1:05:03", 3903000)]
    [InlineData(" 01:25 ", 85000)]      // surrounding whitespace trimmed
    public void Relative_inputs_parse(string input, long expected)
    {
        Assert.True(TimestampParser.TryParse(input, "relative", Started, out long ms));
        Assert.Equal(expected, ms);
    }

    [Fact]
    public void Wallclock_converts_via_the_session_local_start()
    {
        // Mirrors RendererTests.Wallclock_timestamp_adds_offset_to_start in reverse.
        Assert.True(TimestampParser.TryParse("14:33:25", "wallclock", Started, out long ms));
        Assert.Equal(85000, ms);
    }

    [Fact]
    public void Wallclock_wraps_past_midnight()
    {
        var lateStart = new DateTimeOffset(2026, 6, 30, 23, 50, 0, TimeSpan.FromHours(8));
        Assert.True(TimestampParser.TryParse("00:05:12", "wallclock", lateStart, out long ms));
        Assert.Equal(912000, ms);        // 15 min 12 s into the session, next calendar day
    }

    [Fact]
    public void Wallclock_before_start_reads_as_next_day_and_lets_the_caller_clamp()
    {
        // "14:31:00" one minute BEFORE a 14:32:00 start: the deterministic rule is "next day"
        // (23h59m in), which the VM's Playback.Seek clamp then pins to end-of-media. Documented
        // behaviour, not an error - midnight-crossing sessions make earlier-than-start ambiguous.
        Assert.True(TimestampParser.TryParse("14:31:00", "wallclock", Started, out long ms));
        Assert.Equal(86_340_000, ms);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12")]                  // no colon
    [InlineData("1:60")]                // seconds out of range
    [InlineData("1:75:00")]             // minutes out of range when hours present
    [InlineData("1:2:3:4")]             // too many fields
    [InlineData(":")]
    [InlineData("12:")]
    [InlineData("-1:00")]               // signs rejected (NumberStyles.None)
    [InlineData("01.25")]
    [InlineData("999999999999:00")]     // overflow-length field returns false, never throws
    public void Garbage_returns_false_in_relative_mode(string input)
        => Assert.False(TimestampParser.TryParse(input, "relative", Started, out _));

    [Theory]
    [InlineData("14:33")]               // wallclock needs all three fields
    [InlineData("24:00:00")]            // hour out of range
    [InlineData("14:60:00")]
    [InlineData("14:00:60")]
    public void Garbage_returns_false_in_wallclock_mode(string input)
        => Assert.False(TimestampParser.TryParse(input, "wallclock", Started, out _));

    [Fact]
    public void Null_input_returns_false()
        => Assert.False(TimestampParser.TryParse(null, "relative", Started, out _));
}

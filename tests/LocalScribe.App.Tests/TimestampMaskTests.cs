// tests/LocalScribe.App.Tests/TimestampMaskTests.cs
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Credit-card-expiry style auto-colon mask for the read view's go-to box (UX round
/// 2026-08-03): left-anchored, append-only - digits never shift position, a colon appears by
/// itself once a pair completes. Pure/static/WPF-free, so it is exercised directly here without
/// any VM or window scaffolding. Format is the TYPING path (every keystroke); Normalize is the
/// PASTE path only (wired via DataObject.AddPastingHandler in the code-behind) - review fix
/// 2026-08-03: Format alone corrupts a genuine pasted timestamp (see
/// Normalize_round_trips_a_pasted_relative_stamp_back_to_the_same_millisecond below), so paste
/// must go through Normalize first.</summary>
public sealed class TimestampMaskTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("1", "1")]
    [InlineData("14", "14")]
    [InlineData("141", "14:1")]
    [InlineData("1415", "14:15")]
    [InlineData("14153", "14:15:3")]
    [InlineData("141530", "14:15:30")]
    public void Format_inserts_a_colon_after_every_completed_pair_left_anchored(string typed, string expected)
        => Assert.Equal(expected, TimestampMask.Format(typed));

    [Fact]
    public void Format_returns_empty_for_null()
        => Assert.Equal("", TimestampMask.Format(null));

    [Fact]
    public void Format_caps_at_six_digits_and_ignores_the_rest()
    {
        Assert.Equal("12:34:56", TimestampMask.Format("1234567"));
        Assert.Equal("12:34:56", TimestampMask.Format("123456789"));
    }

    [Fact]
    public void Format_has_no_concept_of_time_it_just_flattens_and_repairs_a_flat_digit_run()
    {
        // Format is the typing-path mask only: it has no idea these are hours/minutes/seconds,
        // it just strips every non-digit and re-groups whatever survives into pairs from the
        // left. Fed a genuine timestamp shape (as opposed to raw keystrokes), that can silently
        // produce a DIFFERENT time: "1:02:03" (1h02m03s) flattens to "10203" and re-pairs into
        // "10:20:3" (10h20m3s) - NOT a valid re-parse of the original. That is exactly why paste
        // is routed through Normalize (below) in the view layer, never through Format directly.
        Assert.Equal("14:15", TimestampMask.Format("14:15"));       // already-paired input is a fixed point
        Assert.Equal("10:20:3", TimestampMask.Format("1:02:03"));   // digits re-grouped, NOT re-parsed as a time
        Assert.Equal("12:34", TimestampMask.Format("ab12cd34"));    // arbitrary letters just get stripped
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("hello", "")]
    [InlineData("1:02:03", "01:02:03")]
    [InlineData("9:05", "09:05")]
    [InlineData("14:15", "14:15")]
    [InlineData("1415", "14:15")]
    [InlineData("100:00:00", "100:00:00")]
    public void Normalize_zero_pads_paste_fields_so_the_time_survives_the_paste(string? pasted, string expected)
        => Assert.Equal(expected, TimestampMask.Normalize(pasted));

    [Theory]
    [InlineData("1:02:03")]
    [InlineData("9:05")]
    [InlineData("14:15")]
    [InlineData("1415")]
    public void Normalize_output_is_a_fixed_point_of_Format(string pasted)
    {
        // Why paste (Normalize) and typing (Format) compose without fighting each other: a fully
        // zero-padded stamp of <= 6 digits is a FIXED POINT of the left-anchored pairing mask, so
        // the VM's existing Format pass (which every GoToText write - including a paste's
        // resulting Text - still goes through) leaves a normalized paste untouched.
        // NOTE deliberately excludes "100:00:00" (a >= 100-hour session, out of scope - this app
        // records single calls, not multi-day sessions): Format's 6-digit budget is a flat count
        // over the WHOLE stamp, not field-aware, so a 3-digit hours field pushes it over budget
        // and it gets truncated ("100:00:00" -> "10:00:00"). Normalize itself still preserves it
        // correctly (see Normalize_zero_pads_paste_fields_so_the_time_survives_the_paste above) -
        // this is purely a pre-existing Format limitation for a shape outside the box's realistic
        // range, not a regression this fix introduces or is required to close.
        string normalized = TimestampMask.Normalize(pasted);
        Assert.Equal(normalized, TimestampMask.Format(normalized));
    }

    [Fact]
    public void Normalize_round_trips_a_pasted_relative_stamp_back_to_the_same_millisecond()
    {
        // The regression this fix exists for: TimestampFormat.Stamp renders relative HOURS
        // unpadded ("1:02:03" for 1h02m03s) - so an unpadded shape is exactly what a user copies
        // out of the transcript and pastes back into the go-to box. Before Normalize existed,
        // that pasted text went through Format alone and re-paired into a DIFFERENT time
        // (10h20m3s), which TimestampParser happily accepted with no error shown - the user
        // silently jumped to the wrong place in the recording. Normalize must close that hole.
        var startedAtLocal = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        long originalMs = ((1 * 60 + 2) * 60 + 3) * 1000L;              // 1h02m03s
        string rendered = TimestampFormat.Stamp(originalMs, "relative", startedAtLocal);
        Assert.Equal("1:02:03", rendered);                              // unpadded hour - the bug's trigger

        string normalized = TimestampMask.Normalize(rendered);
        Assert.True(TimestampParser.TryParse(normalized, "relative", startedAtLocal, out long parsedMs));
        Assert.Equal(originalMs, parsedMs);
    }
}

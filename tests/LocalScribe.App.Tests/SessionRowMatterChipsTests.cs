using System;
using System.Collections.Generic;
using System.Linq;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.3 Task 4: each session row exposes a MatterChip per tagged matter id,
/// resolved against SessionsPageViewModel.MatterLookup (Task 2). An id absent from the lookup
/// (deleted matter, lingering tag) falls back to displaying the raw id for both Text and
/// Tooltip - mirrors SessionsPageViewModel.MatterLabel's fallback for the filter dropdown.</summary>
public class SessionRowMatterChipsTests
{
    // Mirrors SessionRowSourceTests.cs's MakeRow (builds the row directly rather than
    // round-tripping through disk/SessionsPageViewModel), extended with matterIds + matterLookup
    // to exercise the new trailing optional ctor param.
    private static SessionRowViewModel MakeRow(
        string[]? matterIds = null,
        IReadOnlyDictionary<string, (string? Reference, string Name)>? matterLookup = null)
    {
        var started = new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero);
        var session = new SessionRecord
        {
            Id = "s-chips", App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(30), UtcOffsetMinutes = 0, DurationMs = 1_800_000,
            Model = "small", Backend = "cpu", Language = "en",
        };
        var meta = new SessionMeta
        {
            Title = "T", Medium = Medium.Webex,
            MatterIds = matterIds ?? [],
        };
        var item = new SessionListItem("s-chips", session, meta);
        return new SessionRowViewModel(item, TimeProvider.System, matterLookup);
    }

    [Fact]
    public void Chips_show_ref_and_name_with_full_tooltip()
    {
        var lookup = new Dictionary<string, (string? Reference, string Name)>(StringComparer.Ordinal)
        {
            ["M-20260705-001"] = ("REF1", "Test 1"),
            ["M-20260705-002"] = (null, "No Ref"),
        };
        var row = MakeRow(matterIds: ["M-20260705-001", "M-20260705-002", "M-ghost-999"], matterLookup: lookup);

        Assert.Equal(3, row.MatterChips.Count);
        Assert.Equal("REF1 Test 1", row.MatterChips[0].Text);
        Assert.Equal("M-20260705-001-REF1 Test 1", row.MatterChips[0].Tooltip);
        Assert.Equal("No Ref", row.MatterChips[1].Text);                 // no ref -> name only
        Assert.Equal("M-20260705-002 No Ref", row.MatterChips[1].Tooltip); // no ref -> "{id} {name}"
        Assert.Equal("M-ghost-999", row.MatterChips[2].Text);            // unknown id -> raw id
        Assert.Equal("M-ghost-999", row.MatterChips[2].Tooltip);         // unknown id -> raw id, both fields
    }
}

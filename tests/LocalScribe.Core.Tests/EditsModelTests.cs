using System.Text.Json;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

public class EditsModelTests
{
    [Fact]
    public void Splits_RoundTripsThroughJson()
    {
        var edits = new Edits
        {
            Splits = new Dictionary<string, SplitEntry>
            {
                ["7"] = new SplitEntry
                {
                    Source = TranscriptSource.Remote,
                    EditedAtUtc = DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
                    Parts =
                    [
                        new SplitPart { Text = "First half.", StartMs = 15000, DerivedStart = false },
                        new SplitPart { Text = "Second half.", StartMs = 16470, DerivedStart = true,
                                        SpeakerParticipantId = "p-2" },
                    ],
                },
            },
        };

        string json = JsonSerializer.Serialize(edits, LocalScribeJson.Options);
        var back = JsonSerializer.Deserialize<Edits>(json, LocalScribeJson.Options)!;

        Assert.Single(back.Splits);
        var entry = back.Splits["7"];
        Assert.Equal(TranscriptSource.Remote, entry.Source);
        Assert.Equal(2, entry.Parts.Count);
        Assert.Equal("Second half.", entry.Parts[1].Text);
        Assert.True(entry.Parts[1].DerivedStart);
        Assert.Equal("p-2", entry.Parts[1].SpeakerParticipantId);
    }

    [Fact]
    public void Splits_DefaultsToEmpty()
        => Assert.Empty(new Edits().Splits);
}

using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
using Xunit;

public class TranscriptProjectionSplitTests
{
    private sealed class NoVocab : IVocabularyProvider
    {
        public string BuildInitialPrompt(IReadOnlyList<string> matterIds) => "";
        public string ApplyCorrections(string text, IReadOnlyList<string> matterIds) => text;
    }

    private static TranscriptProjection New() => new(new NoVocab(), new PhantomBleedDedup());

    [Fact]
    public void SplitSeq_ExpandsIntoChildRows_WithDerivedTimesAndSpeaker()
    {
        var lines = new List<TranscriptLine>
        {
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "First. Second.", "Them"),
        };
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null) with
        {
            RemoteCount = 2,
            Participants =
            [
                new SessionParticipant { Id = "p-2", Name = "Ms. Adams", Side = SourceKind.Remote,
                    Kind = ParticipantKind.Named },
            ],
        };
        var edits = new Edits
        {
            Splits = new Dictionary<string, SplitEntry>
            {
                ["3"] = new SplitEntry
                {
                    Source = TranscriptSource.Remote,
                    Parts =
                    [
                        new SplitPart { Text = "First.", StartMs = 15000, DerivedStart = false },
                        new SplitPart { Text = "Second.", StartMs = 16000, DerivedStart = true,
                                        SpeakerParticipantId = "p-2" },
                    ],
                },
            },
        };

        var rows = New().Build(lines, speakers: null, edits, meta);

        // Two children; the second is a new section because its speaker differs (Ms. Adams).
        var allSegments = rows.SelectMany(r => r.Segments).ToList();
        Assert.Equal(2, allSegments.Count);
        Assert.All(allSegments, s => Assert.True(s.IsSplitChild));
        Assert.Equal(new[] { 0, 1 }, allSegments.Select(s => s.PartIndex));
        Assert.Equal(16000, allSegments[1].StartMs);
        Assert.Equal(17000, allSegments[1].EndMs);          // inherits the line end
        Assert.Contains(rows, r => r.DisplayName == "Ms. Adams");
    }

    [Fact]
    public void UnsplitSession_ProducesOneSegmentPerLine()
    {
        var lines = new List<TranscriptLine>
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 2000, "hello", "Me"),
        };
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null);
        var rows = New().Build(lines, null, new Edits(), meta);
        Assert.Single(rows.SelectMany(r => r.Segments));
        Assert.False(rows[0].HasSplit);
    }
}

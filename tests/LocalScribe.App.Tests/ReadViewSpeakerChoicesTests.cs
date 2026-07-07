// tests/LocalScribe.App.Tests/ReadViewSpeakerChoicesTests.cs
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewSpeakerChoicesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 9, 0, 0, TimeSpan.Zero);

    private static SessionMeta MetaWith(params SessionParticipant[] ps)
        => SessionMeta.CreateDefault(AppKind.Webex, T0, self: null) with
        { RemoteCount = 2, Participants = ps };

    [Fact]
    public void Build_lists_unchanged_then_same_side_named_participant()
    {
        var meta = MetaWith(
            new SessionParticipant { Id = "p-adams", Name = "Ms. Adams", Side = SourceKind.Remote },
            new SessionParticipant { Id = "p-me", Name = "Sam", Side = SourceKind.Local });

        var choices = SpeakerChoices.Build(meta, speakers: null, TranscriptSource.Remote);

        Assert.Equal("(unchanged)", choices[0].Display);
        Assert.Null(choices[0].ParticipantId);
        Assert.Contains(choices, c => c.Display == "Ms. Adams" && c.ParticipantId is not null);
        // Sam is Local-side, so must not appear in the Remote list.
        Assert.DoesNotContain(choices, c => c.Display == "Sam");
    }

    [Fact]
    public void Build_lists_unowned_named_cluster_as_detected_voice()
    {
        var meta = MetaWith(
            new SessionParticipant
            { Id = "p-adams", Name = "Ms. Adams", Side = SourceKind.Remote, ClusterKey = "Remote:0" });
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Remote:0"] = "cluster zero", ["Remote:1"] = "Unknown caller", ["Local:0"] = "local voice" },
        };

        var choices = SpeakerChoices.Build(meta, speakers, TranscriptSource.Remote);

        // Remote:0 is Adams-owned, so it must not be duplicated as a detected voice.
        Assert.DoesNotContain(choices, c => c.ClusterKey == "Remote:0");
        // Remote:1 is unowned and same-side, so it surfaces as a detected voice.
        Assert.Contains(choices, c => c.Display == "Unknown caller (detected voice)" && c.ClusterKey == "Remote:1");
        // Local:0 is the wrong side and must not appear.
        Assert.DoesNotContain(choices, c => c.ClusterKey == "Local:0");
    }
}

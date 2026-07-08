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
    public void Build_leads_with_automatic_then_same_side_named_participant()
    {
        var meta = MetaWith(
            new SessionParticipant { Id = "p-adams", Name = "Ms. Adams", Side = SourceKind.Remote },
            new SessionParticipant { Id = "p-me", Name = "Sam", Side = SourceKind.Local });

        var choices = SpeakerChoices.Build(meta, speakers: null, TranscriptSource.Remote);

        Assert.True(choices[0].IsUnassign);                   // leads with "Automatic (Me / Them)"
        Assert.Equal("Automatic (Me / Them)", choices[0].Display);
        Assert.Contains(choices, c => c.Display == "Ms. Adams" && c.ParticipantId is not null);
        // Sam is Local-side, so must not appear in the Remote list.
        Assert.DoesNotContain(choices, c => c.Display == "Sam");
    }

    [Fact]
    public void Build_offers_exactly_one_automatic_unassign_and_no_unchanged_entry()
    {
        var choices = SpeakerChoices.Build(MetaWith(), speakers: null, TranscriptSource.Local);
        var automatic = Assert.Single(choices, c => c.IsUnassign);
        Assert.Null(automatic.ToPinTarget());                 // no target: it removes a pin, never sets one
        // The confusing separate "(unchanged)" entry is gone - the dropdown pre-selects the current
        // speaker, so leaving that selection IS "unchanged".
        Assert.DoesNotContain(choices, c => c.Display == "(unchanged)");
    }

    [Fact]
    public void CurrentFor_preselects_the_pinned_participant_else_automatic()
    {
        var meta = MetaWith(
            new SessionParticipant { Id = "p-adams", Name = "Ms. Adams", Side = SourceKind.Remote,
                Kind = ParticipantKind.Named, ClusterKey = "Remote:0" });
        var speakers = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0" } },
        };
        var choices = SpeakerChoices.Build(meta, speakers, TranscriptSource.Remote);

        // seq 3 is pinned to the cluster Ms. Adams owns -> pre-select Ms. Adams.
        var forPinned = SpeakerChoices.CurrentFor(3, TranscriptSource.Remote, choices, meta, speakers);
        Assert.Equal("p-adams", forPinned!.ParticipantId);

        // seq 9 isn't pinned -> pre-select the automatic baseline.
        var forBaseline = SpeakerChoices.CurrentFor(9, TranscriptSource.Remote, choices, meta, speakers);
        Assert.True(forBaseline!.IsUnassign);
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

// src/LocalScribe.App/ViewModels/SpeakerChoice.cs
using LocalScribe.App.Services;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable speaker in the Edit-mode dropdown (design §4). Wraps a display label
/// and the resolved target: a participant id or an existing cluster key. Null of both = "no
/// override" (a split child inherits the parent seq's name).</summary>
public sealed record SpeakerChoice(string Display, string? ParticipantId, string? ClusterKey)
{
    public SpeakerPinTarget? ToPinTarget() =>
        ParticipantId is not null ? new SpeakerPinTarget.Participant(ParticipantId)
        : ClusterKey is not null ? new SpeakerPinTarget.Cluster(ClusterKey)
        : null;
}

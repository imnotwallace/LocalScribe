namespace LocalScribe.App.Services;

/// <summary>Who a read-view speaker pin points at (Stage 6.1, design section 1.4): a session
/// participant (identity-first - the pin lands on the participant's owned clusterKey, minting a
/// fresh one and recording ownership when the slot has none yet) or an existing named
/// speakers.json cluster. Closed hierarchy - the private ctor forbids third cases.</summary>
public abstract record SpeakerPinTarget
{
    private SpeakerPinTarget() { }
    public sealed record Participant(string ParticipantId) : SpeakerPinTarget;
    public sealed record Cluster(string ClusterKey) : SpeakerPinTarget;
}

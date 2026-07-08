// src/LocalScribe.App/ViewModels/SpeakerChoice.cs
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable speaker in the Edit-mode dropdown (design §4). Wraps a display label
/// and the resolved target: a participant id or an existing cluster key. Null of both = "no
/// override" (a split child inherits the parent seq's name). IsUnassign is the distinct "Automatic
/// (Me / Them)" choice: it too has no target, but picking it on an already-pinned whole segment
/// REMOVES the pin (falls back to the automatic baseline), where "(unchanged)" leaves the pin as-is.</summary>
public sealed record SpeakerChoice(string Display, string? ParticipantId, string? ClusterKey,
    bool IsUnassign = false)
{
    public SpeakerPinTarget? ToPinTarget() =>
        ParticipantId is not null ? new SpeakerPinTarget.Participant(ParticipantId)
        : ClusterKey is not null ? new SpeakerPinTarget.Cluster(ClusterKey)
        : null;
}

/// <summary>Builds the Edit-mode dropdown's candidate list (design §4). Pure transform, same
/// candidate rule as ReassignSpeakerViewModel: a leading "Automatic (Me / Them)" choice (the
/// automatic baseline; picking it removes a whole segment's pin), then same-side NAMED
/// participants, then named clusters no participant owns. The dropdown is pre-selected to each
/// line's CURRENT speaker (see CurrentFor), so there is no separate "(unchanged)" entry - leaving
/// the pre-selected value IS "unchanged".</summary>
public static class SpeakerChoices
{
    public static IReadOnlyList<SpeakerChoice> Build(SessionMeta meta, Speakers? speakers,
        TranscriptSource source)
    {
        var side = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        var list = new List<SpeakerChoice>
        {
            new("Automatic (Me / Them)", null, null, IsUnassign: true),
        };
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in meta.Participants)
        {
            if (p.Side != side || p.Kind != ParticipantKind.Named || string.IsNullOrEmpty(p.Name)) continue;
            list.Add(new SpeakerChoice(p.Name, p.Id, null));
            if (p.ClusterKey is string ck) owned.Add(ck);
        }
        if (speakers is not null)
        {
            string prefix = source + ":";
            foreach (var (key, name) in speakers.Names)
                if (key.StartsWith(prefix, StringComparison.Ordinal) && !owned.Contains(key))
                    list.Add(new SpeakerChoice($"{name} (detected voice)", null, key));
        }
        return list;
    }

    /// <summary>The choice a line is CURRENTLY attributed to, so entering Edit pre-selects the
    /// dropdown to what is already there (fixes "the selection blanks out"). A seq pinned/diarised
    /// to a clusterKey owned by a NAMED participant -> that participant's choice; to a named cluster
    /// -> that cluster's choice; otherwise (the automatic Me/Them baseline, or a pin whose owner
    /// isn't an offered candidate) -> the "Automatic (Me / Them)" choice. Returned instance is drawn
    /// FROM `choices`, so ComboBox.SelectedItem matches by reference and shows it selected.</summary>
    public static SpeakerChoice? CurrentFor(int seq, TranscriptSource source,
        IReadOnlyList<SpeakerChoice> choices, SessionMeta meta, Speakers? speakers)
    {
        var automatic = choices.FirstOrDefault(c => c.IsUnassign);
        if (speakers is not null
            && speakers.Assignments.TryGetValue(source.ToString(), out var bySeq)
            && bySeq.TryGetValue(seq.ToString(), out var clusterKey))
        {
            var owner = meta.Participants.FirstOrDefault(p => p.ClusterKey == clusterKey
                && p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name));
            if (owner is not null)
            {
                var byOwner = choices.FirstOrDefault(c => c.ParticipantId == owner.Id);
                if (byOwner is not null) return byOwner;
            }
            var byCluster = choices.FirstOrDefault(c => c.ClusterKey == clusterKey);
            if (byCluster is not null) return byCluster;
        }
        return automatic;
    }
}

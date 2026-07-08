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
/// candidate rule as ReassignSpeakerViewModel: a leading "(unchanged)" null-target choice (a split
/// child inherits the parent seq's name), then "Automatic (Me / Them)" which removes a whole
/// segment's pin, then same-side NAMED participants, then named clusters no participant owns.</summary>
public static class SpeakerChoices
{
    public static IReadOnlyList<SpeakerChoice> Build(SessionMeta meta, Speakers? speakers,
        TranscriptSource source)
    {
        var side = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        var list = new List<SpeakerChoice>
        {
            new("(unchanged)", null, null),
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
}

// src/LocalScribe.App/ViewModels/SpeakerChoice.cs
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
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

/// <summary>Builds the Edit-mode dropdown's candidate list (design §4). Pure transform, same
/// candidate rule as ReassignSpeakerViewModel: same-side NAMED participants first, then named
/// clusters no participant owns, plus a leading "(unchanged)" null-target choice so a dropdown
/// can express "no override" (a split child inherits the parent seq's name).</summary>
public static class SpeakerChoices
{
    public static IReadOnlyList<SpeakerChoice> Build(SessionMeta meta, Speakers? speakers,
        TranscriptSource source)
    {
        var side = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        var list = new List<SpeakerChoice> { new("(unchanged)", null, null) };
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

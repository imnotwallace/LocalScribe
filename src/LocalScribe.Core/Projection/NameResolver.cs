using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Resolves a segment's display name per spec section 1.3: pinned/diarised assignment ->
/// single-declared-participant -> baseline Me/Them.</summary>
public static class NameResolver
{
    public static string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta)
    {
        string sourceKey = segment.Source.ToString();          // "Local" / "Remote"
        SourceKind side = segment.Source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;

        // 1) diarisation / pinned assignment
        if (speakers is not null
            && speakers.Assignments.TryGetValue(sourceKey, out var bySeq)
            && bySeq.TryGetValue(segment.Seq.ToString(), out string? clusterKey))
        {
            if (speakers.Names.TryGetValue(clusterKey, out string? named)) return named;
            int colon = clusterKey.IndexOf(':');
            string clusterId = colon >= 0 ? clusterKey[(colon + 1)..] : clusterKey;
            return "Speaker " + clusterId;
        }

        // 2) single declared participant on that side
        int declared = side == SourceKind.Local ? meta.LocalCount : meta.RemoteCount;
        if (declared == 1)
        {
            var only = meta.Participants.FirstOrDefault(p => p.Side == side);
            if (only is not null) return only.Name;
        }

        // 3) baseline label, else derive from source
        if (!string.IsNullOrEmpty(segment.SpeakerLabel)) return segment.SpeakerLabel;
        return side == SourceKind.Local ? "Me" : "Them";
    }
}

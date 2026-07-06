using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Resolves a segment's display name per spec section 1.3 + Stage 5.4 section 5.2:
/// participant-owned cluster -> diarised/pinned assignment name -> single-declared-participant ->
/// baseline Me/Them. Pure projection: reads meta.json/speakers.json models, never writes.</summary>
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
            // 1a) ownership (Stage 5.4 section 5.2): a NAMED slot durably owns the detected
            // voice bound to it - its meta.json Name wins over the speakers.json overlay, so
            // renaming the slot relabels its lines WITHOUT rewriting speakers.json. An Unnamed
            // owner has an empty Name by design and falls through: the design renders unnamed
            // slots "Speaker N", which is exactly the overlay/derived tiers below.
            SessionParticipant? owner = meta.Participants.FirstOrDefault(p =>
                p.ClusterKey == clusterKey
                && p.Kind == ParticipantKind.Named
                && !string.IsNullOrEmpty(p.Name));
            if (owner is not null) return owner.Name;

            // 1b) speakers.json name overlay, else the derived per-cluster label.
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

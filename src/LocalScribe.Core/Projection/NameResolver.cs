using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>Resolves a segment's display name per spec section 1.3 + Stage 5.4 section 5.2:
/// participant-owned cluster -> diarised/pinned assignment name -> single-declared-participant ->
/// baseline Me/Them. Pure projection: reads meta.json/speakers.json models, never writes.</summary>
public static class NameResolver
{
    public static string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta)
        => Resolve(segment, speakers, meta, participantIdOverride: null, clusterKeyOverride: null);

    public static string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta,
        string? participantIdOverride, string? clusterKeyOverride)
    {
        // Split-child speaker overrides (design section 2.4) win first.
        if (!string.IsNullOrEmpty(participantIdOverride))
        {
            var p = meta.Participants.FirstOrDefault(x => x.Id == participantIdOverride);
            if (p is not null && !string.IsNullOrEmpty(p.Name)) return p.Name;
        }
        if (!string.IsNullOrEmpty(clusterKeyOverride))
            return ResolveClusterKey(clusterKeyOverride, speakers, meta);

        string sourceKey = segment.Source.ToString();          // "Local" / "Remote"
        SourceKind side = segment.Source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;

        // 1) diarisation / pinned assignment
        if (speakers is not null
            && speakers.Assignments.TryGetValue(sourceKey, out var bySeq)
            && bySeq.TryGetValue(segment.Seq.ToString(), out string? clusterKey))
            return ResolveClusterKey(clusterKey, speakers, meta);

        // 2) single declared voice on that side: only a LONE NAMED slot may label the whole
        // side (Stage 5.4 section 5.2). Declared counts equal the side's slot count
        // (Named + Unnamed) once the editor commits; an Unnamed-only side with count 1 has no
        // name to project and stays baseline. Two Named slots with declared==1 is an
        // inconsistent/transitional state - never pick one arbitrarily (no speculative
        // attribution). Unnamed slots are ignored by the "exactly one" check.
        int declared = side == SourceKind.Local ? meta.LocalCount : meta.RemoteCount;
        if (declared == 1)
        {
            SessionParticipant? lone = null;
            int namedOnSide = 0;
            foreach (var p in meta.Participants)
                if (p.Side == side && p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name))
                { namedOnSide++; lone = p; }
            if (namedOnSide == 1) return lone!.Name;
        }

        // 3) baseline label, else derive from source
        if (!string.IsNullOrEmpty(segment.SpeakerLabel)) return segment.SpeakerLabel;
        return side == SourceKind.Local ? "Me" : "Them";
    }

    // 1a) ownership (Stage 5.4 section 5.2): a NAMED slot durably owns the detected voice
    // bound to it - its meta.json Name wins over the speakers.json overlay, so renaming the
    // slot relabels its lines WITHOUT rewriting speakers.json. An Unnamed owner has an empty
    // Name by design and falls through: the design renders unnamed slots "Speaker N", which
    // is exactly the overlay/derived tiers below.
    // 1b) speakers.json name overlay, else the derived per-cluster label.
    // Extracted verbatim so a split-child clusterKey override resolves the same way.
    private static string ResolveClusterKey(string clusterKey, Speakers? speakers, SessionMeta meta)
    {
        SessionParticipant? owner = meta.Participants.FirstOrDefault(p =>
            p.ClusterKey == clusterKey
            && p.Kind == ParticipantKind.Named
            && !string.IsNullOrEmpty(p.Name));
        if (owner is not null) return owner.Name;

        if (speakers is not null && speakers.Names.TryGetValue(clusterKey, out string? named)) return named;
        int colon = clusterKey.IndexOf(':');
        string clusterId = colon >= 0 ? clusterKey[(colon + 1)..] : clusterKey;
        return "Speaker " + clusterId;
    }
}

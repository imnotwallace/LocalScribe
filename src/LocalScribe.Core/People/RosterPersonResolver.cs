using LocalScribe.Core.Model;

namespace LocalScribe.Core.People;

/// <summary>The ONE rule that turns a Matter roster into global <see cref="Person"/> links
/// (voiceprint design 2026-07-25; final whole-branch review finding I1). Shared by all three
/// consumers so they cannot drift: the Split dialog's default matter-scoped suggestion pool, its
/// confirm-time enrollment rule 2, and the Settings backfill scan's roster map.
///
/// <see cref="RosterMember.PersonId"/> is the precise link and ALWAYS wins where it is set - but
/// nothing in the product writes one yet (there is no link UI), so on its own it left the
/// matter-scoped pool permanently empty, the confirm-time roster rule dead, and the backfill's
/// roster map always falling through to the global name fallback. A roster member with no
/// PersonId is therefore resolved by EXACT-ORDINAL match on <see cref="Person.Name"/> - the same
/// rule <see cref="VoiceprintEnrollmentService"/> already applied - and that fallback is what makes
/// any of this reachable today.
///
/// Safe by construction: the output is only ever a SUGGESTION pool or an enrollment target the
/// user has explicitly confirmed. No path here assigns a name, and none of it touches audio,
/// transcripts, or speaker names. When an explicit link is added later it simply takes precedence
/// and the fallback stops mattering for that member.</summary>
public static class RosterPersonResolver
{
    /// <summary>Roster member NAME -> Person id, first link wins per name. Keyed by name because
    /// its consumers match a typed/effective speaker name against the roster.</summary>
    public static IReadOnlyDictionary<string, string> LinkByName(
        IEnumerable<RosterMember> roster, PeopleRegistry? registry)
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        var explicitlyLinked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in roster)
        {
            if (string.IsNullOrWhiteSpace(member.Name)) continue;
            if (member.PersonId is string personId)
            {
                // An explicit link beats a name match for the same name whatever the roster order,
                // so it may overwrite an entry an earlier name-matched member put here.
                if (explicitlyLinked.Add(member.Name)) byName[member.Name] = personId;
                continue;
            }
            if (explicitlyLinked.Contains(member.Name) || byName.ContainsKey(member.Name)) continue;
            if (registry is not null && PeopleRegistryOps.FindByName(registry, member.Name) is { } person)
                byName[member.Name] = person.Id;
        }
        return byName;
    }

    /// <summary>Every Person id this roster points at - the matter-scoped suggestion pool. NOT
    /// derived from <see cref="LinkByName"/>: a member with an explicit PersonId contributes ONLY
    /// that person, never also a same-named stranger, and two members sharing a name but carrying
    /// different explicit ids both count.</summary>
    public static HashSet<string> PersonIds(
        IEnumerable<RosterMember> roster, PeopleRegistry? registry)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in roster)
        {
            if (member.PersonId is string personId) { ids.Add(personId); continue; }
            if (string.IsNullOrWhiteSpace(member.Name) || registry is null) continue;
            if (PeopleRegistryOps.FindByName(registry, member.Name) is { } person) ids.Add(person.Id);
        }
        return ids;
    }
}

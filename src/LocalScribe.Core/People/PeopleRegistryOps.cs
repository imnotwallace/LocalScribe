using LocalScribe.Core.Model;
namespace LocalScribe.Core.People;

/// <summary>Pure transformations over <see cref="PeopleRegistry"/> (voiceprint design
/// 2026-07-25). All methods return a new registry; inputs are never mutated. Person lookup by
/// name is EXACT ordinal - the same rule the Split dialog uses to match candidate names.</summary>
public static class PeopleRegistryOps
{
    public const int MaxEnrollmentsPerPerson = 20;

    public static Person? FindByName(PeopleRegistry reg, string name)
        => reg.People.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>True when <paramref name="personId"/> names a Person currently in the registry
    /// (fix round 1, finding C). <see cref="Enroll"/>'s underlying <see cref="Update"/> silently
    /// no-ops on an unknown id - a stale roster/request PersonId would otherwise pass every null
    /// guard, drive a real embed call, and "enroll" into nothing while callers' counters/changed
    /// flags still advance as if it worked. Callers must check this BEFORE enrolling, not rely on
    /// Enroll's no-op to make it safe.</summary>
    public static bool Exists(PeopleRegistry reg, string personId)
        => reg.People.Any(p => p.Id == personId);

    public static (PeopleRegistry Registry, Person Person) EnsurePerson(
        PeopleRegistry reg, string name, Func<string> newId, DateTimeOffset now)
    {
        var existing = FindByName(reg, name);
        if (existing is not null) return (reg, existing);
        var person = new Person { Id = newId(), Name = name, CreatedUtc = now };
        return (reg with { People = [.. reg.People, person] }, person);
    }

    public static PeopleRegistry Enroll(PeopleRegistry reg, string personId, VoiceprintEnrollment e)
        => Update(reg, personId, p =>
        {
            var list = new List<VoiceprintEnrollment>(p.Voiceprint) { e };
            while (list.Count > MaxEnrollmentsPerPerson) list.RemoveAt(0);   // FIFO eviction
            return p with { Voiceprint = list };
        });

    public static PeopleRegistry RemoveEnrollment(PeopleRegistry reg, string personId, string enrollmentId)
        => Update(reg, personId, p => p with
        { Voiceprint = p.Voiceprint.Where(e => e.Id != enrollmentId).ToList() });

    public static PeopleRegistry DeleteVoiceprint(PeopleRegistry reg, string personId)
        => Update(reg, personId, p => p with { Voiceprint = [] });

    public static PeopleRegistry RemovePerson(PeopleRegistry reg, string personId)
        => reg with { People = reg.People.Where(p => p.Id != personId).ToList() };

    public static PeopleRegistry ClearAllVoiceprints(PeopleRegistry reg)
        => reg with { People = reg.People.Select(p => p with { Voiceprint = [] }).ToList() };

    private static PeopleRegistry Update(PeopleRegistry reg, string personId, Func<Person, Person> f)
        => reg with { People = reg.People.Select(p => p.Id == personId ? f(p) : p).ToList() };
}

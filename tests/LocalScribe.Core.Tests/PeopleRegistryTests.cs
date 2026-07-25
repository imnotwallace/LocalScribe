using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;

public class PeopleRegistryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lspeople").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static VoiceprintEnrollment E(string id) => new()
    {
        Id = id, Embedding = [1f], Method = "campplus-zh-en",
        SourceSessionId = "s1", SourceClusterKey = "Remote:0",
        EnrolledAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void EnsurePerson_creates_then_matches_exact_name()
    {
        var (reg, p1) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "Sarah Chen",
            () => "p1", DateTimeOffset.UnixEpoch);
        var (reg2, p2) = PeopleRegistryOps.EnsurePerson(reg, "Sarah Chen", () => "p2", DateTimeOffset.UnixEpoch);
        Assert.Equal("p1", p1.Id);
        Assert.Equal("p1", p2.Id);                       // matched, not re-created
        Assert.Single(reg2.People);
        Assert.Null(PeopleRegistryOps.FindByName(reg2, "sarah chen"));  // ordinal, not ci
    }

    [Fact]
    public void Enroll_appends_and_caps_at_20_evicting_oldest()
    {
        var (reg, p) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        for (int i = 0; i < 25; i++) reg = PeopleRegistryOps.Enroll(reg, "p1", E($"e{i}"));
        var vp = reg.People.Single().Voiceprint;
        Assert.Equal(20, vp.Count);
        Assert.Equal("e5", vp[0].Id);                    // e0..e4 evicted
        Assert.Equal("e24", vp[^1].Id);
    }

    [Fact]
    public void Enroll_unknown_person_is_noop()
    {
        var reg = PeopleRegistryOps.Enroll(new PeopleRegistry(), "ghost", E("e1"));
        Assert.Empty(reg.People);
    }

    [Fact]
    public void Deletes_enrollment_voiceprint_and_person()
    {
        var (reg, _) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        reg = PeopleRegistryOps.Enroll(reg, "p1", E("e1"));
        reg = PeopleRegistryOps.Enroll(reg, "p1", E("e2"));

        var afterOne = PeopleRegistryOps.RemoveEnrollment(reg, "p1", "e1");
        Assert.Single(afterOne.People.Single().Voiceprint);

        var afterVp = PeopleRegistryOps.DeleteVoiceprint(reg, "p1");
        Assert.Empty(afterVp.People.Single().Voiceprint);
        Assert.Equal("A", afterVp.People.Single().Name);   // person survives

        var afterPerson = PeopleRegistryOps.RemovePerson(reg, "p1");
        Assert.Empty(afterPerson.People);
    }

    [Fact]
    public void ClearAllVoiceprints_strips_every_enrollment_keeps_people()
    {
        var (reg, _) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        (reg, _) = PeopleRegistryOps.EnsurePerson(reg, "B", () => "p2", DateTimeOffset.UnixEpoch);
        reg = PeopleRegistryOps.Enroll(reg, "p1", E("e1"));
        reg = PeopleRegistryOps.Enroll(reg, "p2", E("e2"));
        var cleared = PeopleRegistryOps.ClearAllVoiceprints(reg);
        Assert.Equal(2, cleared.People.Count);
        Assert.All(cleared.People, p => Assert.Empty(p.Voiceprint));
    }

    [Fact]
    public async Task Store_round_trips_and_rejects_newer_schema()
    {
        var path = Path.Combine(_dir, "people.json");
        var store = new PeopleStore(path);
        var (reg, _) = PeopleRegistryOps.EnsurePerson(new PeopleRegistry(), "A", () => "p1", DateTimeOffset.UnixEpoch);
        await store.SaveAsync(PeopleRegistryOps.Enroll(reg, "p1", E("e1")), default);
        var back = await store.LoadAsync(default);
        Assert.Equal(1f, back!.People.Single().Voiceprint.Single().Embedding[0]);

        await File.WriteAllTextAsync(path, "{\"schemaVersion\":99}");
        await Assert.ThrowsAsync<NotSupportedException>(() => store.LoadAsync(default));
    }

    [Fact]
    public void RosterMember_carries_optional_PersonId()
    {
        var m = new RosterMember { Id = "r1", Name = "A", PersonId = "p1" };
        Assert.Equal("p1", m.PersonId);
        Assert.Null(new RosterMember().PersonId);
    }
}

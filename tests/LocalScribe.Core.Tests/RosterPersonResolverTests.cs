using LocalScribe.Core.Model;
using LocalScribe.Core.People;

/// <summary>The one rule that turns a matter roster into a set of global Person links (voiceprint
/// design 2026-07-25, final whole-branch review finding I1).
///
/// <see cref="RosterMember.PersonId"/> has no writer anywhere in the product yet - no UI creates an
/// explicit link - so before this resolver existed the matter-scoped suggestion pool was
/// PERMANENTLY EMPTY and the confirm-time roster rule was dead code. The resolver falls back to an
/// exact-ordinal name match against a saved Person, the same rule the backfill scan already used,
/// while keeping an explicit PersonId strictly ahead of it.</summary>
public class RosterPersonResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private static Person P(string id, string name) => new() { Id = id, Name = name, CreatedUtc = T0 };

    private static PeopleRegistry Registry(params Person[] people) => new() { People = people };

    [Fact]
    public void Roster_member_without_person_id_resolves_by_exact_name()
    {
        var links = RosterPersonResolver.LinkByName(
            [new RosterMember { Id = "r1", Name = "Sarah Chen" }],
            Registry(P("p1", "Sarah Chen")));

        Assert.Equal("p1", links["Sarah Chen"]);
    }

    [Fact]
    public void Explicit_person_id_wins_over_a_same_named_person_in_either_order()
    {
        // The namesake is FIRST in the registry, so a name match would resolve to it. An explicit
        // link must win regardless of which roster member the loop sees first.
        var registry = Registry(P("p-namesake", "Sarah Chen"), P("p-explicit", "Sarah Chen"));

        var explicitFirst = RosterPersonResolver.LinkByName(
            [
                new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p-explicit" },
                new RosterMember { Id = "r2", Name = "Sarah Chen" },
            ], registry);
        Assert.Equal("p-explicit", explicitFirst["Sarah Chen"]);

        var explicitSecond = RosterPersonResolver.LinkByName(
            [
                new RosterMember { Id = "r2", Name = "Sarah Chen" },
                new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p-explicit" },
            ], registry);
        Assert.Equal("p-explicit", explicitSecond["Sarah Chen"]);
    }

    [Fact]
    public void Name_match_is_exact_ordinal_and_an_unmatched_name_links_to_nothing()
    {
        var links = RosterPersonResolver.LinkByName(
            [
                new RosterMember { Id = "r1", Name = "sarah chen" },   // case differs
                new RosterMember { Id = "r2", Name = "Nobody Saved" },
                new RosterMember { Id = "r3", Name = "  " },           // blank name is not a link
            ],
            Registry(P("p1", "Sarah Chen")));

        Assert.Empty(links);
    }

    [Fact]
    public void Null_registry_still_yields_the_explicit_links()
    {
        var links = RosterPersonResolver.LinkByName(
            [
                new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p-explicit" },
                new RosterMember { Id = "r2", Name = "Unlinked" },
            ], registry: null);

        Assert.Equal("p-explicit", Assert.Single(links).Value);
    }

    [Fact]
    public void PersonIds_takes_the_explicit_link_and_never_its_namesake()
    {
        // The pool must contain exactly the people the roster actually points at. Including BOTH
        // the explicit link and the same-named stranger would make two identical-scoring
        // candidates and (by the matcher's margin rule) suppress the suggestion entirely.
        var ids = RosterPersonResolver.PersonIds(
            [
                new RosterMember { Id = "r1", Name = "Sarah Chen", PersonId = "p-explicit" },
                new RosterMember { Id = "r2", Name = "Bob Smith" },
                new RosterMember { Id = "r3", Name = "Nobody Saved" },
            ],
            Registry(P("p-namesake", "Sarah Chen"), P("p-explicit", "Sarah Chen"), P("p-bob", "Bob Smith")));

        Assert.Equal(new[] { "p-bob", "p-explicit" }, ids.OrderBy(i => i, StringComparer.Ordinal).ToArray());
    }
}

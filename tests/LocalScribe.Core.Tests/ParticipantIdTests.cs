using LocalScribe.Core.Storage;

public class ParticipantIdTests
{
    [Fact]
    public void Mints_ascii_slug_with_p_prefix()
        => Assert.Equal("p-alice-client", ParticipantId.Mint("Alice Client", []));

    [Fact]
    public void Trims_lowercases_and_collapses_separators()
        => Assert.Equal("p-alice-obrien", ParticipantId.Mint("  Alice   O'Brien ", []));

    [Fact]
    public void Collision_appends_2_then_3()
    {
        Assert.Equal("p-alice-client-2",
            ParticipantId.Mint("Alice Client", ["p-alice-client"]));
        Assert.Equal("p-alice-client-3",
            ParticipantId.Mint("Alice Client", ["p-alice-client", "p-alice-client-2"]));
    }

    [Fact]
    public void Non_ascii_only_name_slugs_to_person()
    {
        // "\u5F20\u4F1F" (CJK name) has no ASCII letters/digits at all.
        Assert.Equal("p-person", ParticipantId.Mint("\u5F20\u4F1F", []));
        Assert.Equal("p-person", ParticipantId.Mint("###", []));
        Assert.Equal("p-person-2", ParticipantId.Mint("\u5F20\u4F1F", ["p-person"]));
    }

    [Fact]
    public void P_self_is_reserved_and_never_minted()
    {
        // Even with no existing ids, a name slugging to "self" must not claim p-self
        // (SessionBootstrap.cs:24 mints p-self for the user themselves).
        Assert.Equal("p-self-2", ParticipantId.Mint("Self", []));
        Assert.Equal("p-self-2", ParticipantId.Mint("SELF", []));
    }
}

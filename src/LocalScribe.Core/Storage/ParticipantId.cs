namespace LocalScribe.Core.Storage;

/// <summary>Roster-member / session-participant id minting (design 4.2, spec section 1.5):
/// "p-{ascii-slug}" with -2/-3 suffixes on collision; uniqueness is scoped to the owning
/// matter roster or session participant list (the ids passed in). "p-self" is reserved by
/// SessionBootstrap and is never minted here. Names with no ASCII letters/digits slug to
/// "p-person" - the snapshot Name keeps the real (possibly non-ASCII) spelling.</summary>
public static class ParticipantId
{
    public static string Mint(string name, IReadOnlyCollection<string> existingIds)
    {
        string candidate = "p-" + SessionId.Slug(name, "person");
        return SessionId.EnsureUnique(candidate, id => id == "p-self" || existingIds.Contains(id));
    }
}

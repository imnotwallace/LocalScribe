using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class StoragePathsTests
{
    [Fact]
    public void Root_expands_env_and_is_absolute_with_spec_layout()
    {
        var p = new StoragePaths("%USERPROFILE%/LocalScribe");
        Assert.True(Path.IsPathFullyQualified(p.Root));
        Assert.DoesNotContain("%", p.Root);
        Assert.EndsWith("LocalScribe", p.Root.TrimEnd('\\', '/'));
        Assert.Equal(Path.Combine(p.Root, "sessions"), p.SessionsDir);
        Assert.Equal(Path.Combine(p.Root, "matters"), p.MattersDir);
    }

    [Fact]
    public void Per_file_paths_follow_section_9()
    {
        var p = new StoragePaths(@"C:\Data\LocalScribe");
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\transcript.jsonl", p.TranscriptJsonl("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\session.json", p.SessionJson("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\session.txt", p.SessionTxt("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\matters\matters.json", p.MattersIndexJson);
        Assert.Equal(@"C:\Data\LocalScribe\matters\M-1\matter.json", p.MatterJson("M-1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\local.flac", p.AudioFile("s1", SourceKind.Local, AudioFormat.Flac));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\remote.wav", p.AudioFile("s1", SourceKind.Remote, AudioFormat.Wav));
    }

    [Fact]
    public void SessionId_uses_local_wall_clock_time()
    {
        // Spec 1.2 example: started 06:32:05Z at +08:00 (Singapore) -> local 14:32 -> id 1432.
        var startedLocal = new DateTimeOffset(2026, 7, 2, 14, 32, 5, TimeSpan.FromHours(8));
        Assert.Equal("2026-07-02_1432_Webex_doe-intake",
            SessionId.New(startedLocal, AppKind.Webex, "Doe intake"));
    }

    [Theory]
    [InlineData("Doe v. State", "doe-v-state")]
    [InlineData("  Weekly  Sync!! ", "weekly-sync")]
    [InlineData("***", "session")]
    // Apostrophes are elided (not treated as a separator) so session titles like
    // "O'Brien deposition" slug to "obrien-deposition", not "o-brien-deposition" -
    // this is the desired session-title behavior per spec section 9 (sign-off: same
    // Slug loop backs both SessionId.New and ParticipantId.Mint; pin it here too,
    // not just indirectly via ParticipantIdTests).
    [InlineData("O'Brien deposition", "obrien-deposition")]
    public void Slug_normalizes(string input, string expected)
        => Assert.Equal(expected, SessionId.Slug(input));

    [Fact]
    public void EnsureUnique_returns_candidate_or_first_free_numeric_suffix()
    {
        Assert.Equal("2026-07-02_1432_Webex_doe-intake",
            SessionId.EnsureUnique("2026-07-02_1432_Webex_doe-intake", _ => false));

        var taken = new HashSet<string> { "2026-07-02_1432_Webex_doe-intake", "2026-07-02_1432_Webex_doe-intake-2" };
        Assert.Equal("2026-07-02_1432_Webex_doe-intake-3",
            SessionId.EnsureUnique("2026-07-02_1432_Webex_doe-intake", taken.Contains));
    }

    [Theory]
    [InlineData(@"C:\Users\sam\OneDrive\LocalScribe", true, "OneDrive")]
    [InlineData(@"C:\Users\sam\OneDrive - Contoso\LocalScribe", true, "OneDrive")]
    [InlineData(@"C:\Users\sam\Dropbox\LocalScribe", true, "Dropbox")]
    [InlineData(@"C:\Users\sam\LocalScribe", false, null)]
    public void SyncProviderCheck_flags_known_providers(string path, bool expected, string? provider)
    {
        bool got = SyncProviderCheck.ResolvesUnderSyncProvider(path, out string? p);
        Assert.Equal(expected, got);
        Assert.Equal(provider, p);
    }
}

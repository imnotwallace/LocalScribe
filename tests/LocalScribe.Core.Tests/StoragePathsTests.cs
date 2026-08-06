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

    [Fact]
    public void Version_paths_resolve_v1_to_the_session_root_and_others_under_versions()
    {
        var p = new StoragePaths(@"C:\Data\LocalScribe");
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions", p.VersionsDir("s1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1", p.VersionDir("s1", "v1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions\v2-base.en-2026-07-13",
            p.VersionDir("s1", "v2-base.en-2026-07-13"));
        // "v1" overloads are byte-identical to the root getters (the pre-versioning layout).
        Assert.Equal(p.TranscriptJsonl("s1"), p.TranscriptJsonl("s1", "v1"));
        Assert.Equal(p.EditsJson("s1"), p.EditsJson("s1", "v1"));
        Assert.Equal(p.SpeakersJson("s1"), p.SpeakersJson("s1", "v1"));
        Assert.Equal(p.TranscriptMd("s1"), p.TranscriptMd("s1", "v1"));
        Assert.Equal(p.TranscriptTxt("s1"), p.TranscriptTxt("s1", "v1"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions\v2-base.en-2026-07-13\transcript.jsonl",
            p.TranscriptJsonl("s1", "v2-base.en-2026-07-13"));
        Assert.Equal(@"C:\Data\LocalScribe\sessions\s1\versions\v2-base.en-2026-07-13\edits.json",
            p.EditsJson("s1", "v2-base.en-2026-07-13"));
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

    [Fact]
    public void Diagnostics_live_in_their_own_derived_folder_beside_sessions_and_matters()
    {
        var p = new StoragePaths(@"C:\Data\LocalScribe");
        Assert.Equal(@"C:\Data\LocalScribe\diagnostics", p.DiagnosticsDir);
        // Deliberately NOT "logs" (Tier 1 plan A, 2026-08-05): .gitignore already swallows
        // [Ll]og/, [Ll]ogs/ and *.log, so a logs\ folder created during a test run would vanish
        // from git status and a stray artefact could never be noticed.
        Assert.DoesNotContain("log", p.DiagnosticsDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_lives_beside_the_transcript_it_seals_in_every_version_layout()
    {
        // Tier 1 T1-7: the manifest seals a VERSION's transcript/edits/speakers, so it lives in
        // that version's folder. "v1" degenerates to the session root exactly like every other
        // version-aware getter, so a pre-versioning session needs no special case.
        var paths = new StoragePaths(@"C:\root");
        Assert.Equal(Path.Combine(paths.SessionDir("s1"), "manifest.json"), paths.ManifestJson("s1"));
        Assert.Equal(paths.ManifestJson("s1"), paths.ManifestJson("s1", TranscriptVersions.Root));
        Assert.Equal(Path.Combine(paths.VersionDir("s1", "v2-x"), "manifest.json"),
            paths.ManifestJson("s1", "v2-x"));
    }
}

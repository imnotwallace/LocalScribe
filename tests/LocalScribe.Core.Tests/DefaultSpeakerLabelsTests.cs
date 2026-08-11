using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;

/// <summary>Pins DefaultSpeakerLabels.IsMachineDefault, the predicate that decides whether a
/// speakers.json name is a HUMAN act (whole-branch review finding 1). It exists because
/// SpeakerDetectionStep commits DefaultSpeakerLabels.For(...) output into speakers.json Names on
/// every auto-detected import with no user interaction at all, so "Names.Count > 0" was never
/// evidence a person touched the transcript - and the exported disclaimer is chosen from it.</summary>
public class DefaultSpeakerLabelsTests
{
    [Theory]
    [InlineData("Local:0", "Local Speaker 1")]
    [InlineData("Local:4", "Local Speaker 5")]
    [InlineData("Remote:0", "Remote Speaker 1")]
    public void A_name_equal_to_the_machine_default_is_not_a_human_act(string key, string name)
        // Exactly what SpeakerDetectionStep writes for an auto-detected import.
        => Assert.True(DefaultSpeakerLabels.IsMachineDefault(key, name));

    [Theory]
    [InlineData("Local:0", "Ms Chen")]
    [InlineData("Local:0", "Local Speaker 2")]     // right shape, WRONG cluster: a person retyped it
    [InlineData("Remote:1", "local speaker 2")]    // case differs: Ordinal, never case-insensitive
    public void A_name_a_person_typed_is_a_human_act(string key, string name)
        => Assert.False(DefaultSpeakerLabels.IsMachineDefault(key, name));

    [Fact]
    public void A_mixed_map_counts_only_the_renamed_cluster()
    {
        // The realistic post-import state: the diariser named both, the user renamed one.
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Local:0"] = "Local Speaker 1",
            ["Local:1"] = "Ms Chen",
        };
        Assert.Equal(1, names.Count(n => !DefaultSpeakerLabels.IsMachineDefault(n.Key, n.Value)));
    }

    [Theory]
    [InlineData("", "Ms Chen")]
    [InlineData("Local", "Ms Chen")]              // no separator at all
    [InlineData("Local:", "Ms Chen")]             // separator, no id
    [InlineData("Local:abc", "Ms Chen")]          // id is not an integer
    [InlineData("Martian:0", "Ms Chen")]          // source prefix this build has never heard of
    [InlineData("0:1", "Ms Chen")]                // Enum.TryParse would accept "0" as Local - it must not
    public void An_unresolvable_clusterKey_never_claims_a_person_named_it(string key, string name)
        // Safe direction for an evidentiary document: with no computable default, "differs from the
        // default" would be vacuously true and every such key would flip the disclaimer to
        // "reviewed and corrected by a person". Under-counting keeps the UNTOUCHED sentence, which
        // stays literally true even for a transcript a person did edit. See IsMachineDefault's doc.
        => Assert.True(DefaultSpeakerLabels.IsMachineDefault(key, name));

    [Theory]
    [InlineData("Local:0", "")]
    [InlineData("Local:0", "   ")]
    public void A_blank_name_is_not_a_name_a_person_chose(string key, string name)
        => Assert.True(DefaultSpeakerLabels.IsMachineDefault(key, name));

    [Fact]
    public void Out_of_range_ids_do_not_throw_and_stay_on_the_conservative_side()
    {
        // A hand-edited or older-build key must never fault an export. A negative id is treated as
        // unresolvable, not resolved: ids are 0-based and allocated as maxId+1, so no run mints
        // one, and For(Local, -1) would answer "Local Speaker 0" - a label no machine can produce,
        // which would make EVERY name under such a key read as human-chosen.
        Assert.True(DefaultSpeakerLabels.IsMachineDefault("Local:-1", "Ms Chen"));
        Assert.True(DefaultSpeakerLabels.IsMachineDefault("Local:99999999999999999999", "Ms Chen"));
        // int.MaxValue is in range and therefore resolved normally - the point is that it answers
        // rather than throwing, even though the 1-based label wraps.
        Assert.False(DefaultSpeakerLabels.IsMachineDefault(
            "Local:" + int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "Ms Chen"));
    }

    [Fact]
    public void The_predicate_agrees_with_For_across_the_whole_enum()
    {
        // Guard against a new SourceKind member being added without teaching the resolver about it:
        // whatever For() generates for a source must read back as a machine default.
        foreach (var source in Enum.GetValues<SourceKind>())
            Assert.True(DefaultSpeakerLabels.IsMachineDefault(
                $"{source}:2", DefaultSpeakerLabels.For(source, 2)));
    }
}

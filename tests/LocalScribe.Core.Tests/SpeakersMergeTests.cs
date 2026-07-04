using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;

public class SpeakersMergeTests
{
    private static DiarisationCommit Commit(
        IReadOnlyDictionary<string, string> remoteSeqs,
        IReadOnlyDictionary<string, string> names) =>
        new([SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>> { ["Remote"] = remoteSeqs },
            names, "sherpa", DateTimeOffset.UnixEpoch);

    [Fact]
    public void First_run_writes_assignments_names_sources_method()
    {
        var commit = Commit(
            new Dictionary<string, string> { ["3"] = "Remote:0", ["4"] = "Remote:1" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" });

        var merged = SpeakersMerge.Merge(null, commit);

        Assert.Equal("Remote:0", merged.Assignments["Remote"]["3"]);
        Assert.Equal("Remote Speaker 2", merged.Names["Remote:1"]);
        Assert.Contains(SourceKind.Remote, merged.DiarisedSources);
        Assert.Equal("sherpa", merged.Method);
        Assert.Equal(DateTimeOffset.UnixEpoch, merged.DiarisedAtUtc);
    }

    [Fact]
    public void Rediarise_preserves_pinned_assignment_and_its_name_verbatim()
    {
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["3"] = "Remote:9", ["4"] = "Remote:0" } },
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["3"] },  // seq 3 pinned
            Names = new Dictionary<string, string> { ["Remote:9"] = "Judge Wu", ["Remote:0"] = "Remote Speaker 1" },
            DiarisedSources = [SourceKind.Remote],
        };
        // New run reassigns seq 4 and produces different cluster ids; seq 3 must not move.
        var commit = Commit(
            new Dictionary<string, string> { ["4"] = "Remote:1" },
            new Dictionary<string, string> { ["Remote:1"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);

        Assert.Equal("Remote:9", merged.Assignments["Remote"]["3"]);   // pinned kept
        Assert.Equal("Judge Wu", merged.Names["Remote:9"]);            // pinned name kept
        Assert.Equal("Remote:1", merged.Assignments["Remote"]["4"]);   // non-pinned reset to new run
    }

    [Fact]
    public void Rediarise_drops_non_pinned_names_so_stale_name_cannot_rebind()
    {
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["4"] = "Remote:0" } },
            Names = new Dictionary<string, string> { ["Remote:0"] = "Alice" },   // NOT pinned
            DiarisedSources = [SourceKind.Remote],
        };
        var commit = Commit(
            new Dictionary<string, string> { ["4"] = "Remote:0" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);
        // The stale "Alice" must be gone; only the new run's default remains.
        Assert.Equal("Remote Speaker 1", merged.Names["Remote:0"]);
    }

    [Fact]
    public void Other_source_data_is_untouched()
    {
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Local"] = new() { ["1"] = "Local:0" } },
            Names = new Dictionary<string, string> { ["Local:0"] = "Me-A" },
            DiarisedSources = [SourceKind.Local],
        };
        var commit = Commit(
            new Dictionary<string, string> { ["3"] = "Remote:0" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);
        Assert.Equal("Local:0", merged.Assignments["Local"]["1"]);   // Local untouched
        Assert.Equal("Me-A", merged.Names["Local:0"]);
        Assert.Contains(SourceKind.Local, merged.DiarisedSources);
        Assert.Contains(SourceKind.Remote, merged.DiarisedSources);
        // ...and the re-diarised source's own fresh result actually lands.
        Assert.Equal("Remote:0", merged.Assignments["Remote"]["3"]);
        Assert.Equal("Remote Speaker 1", merged.Names["Remote:0"]);
    }

    [Fact]
    public void DefaultSpeakerLabels_are_one_based_and_per_side()
    {
        Assert.Equal("Remote Speaker 1", DefaultSpeakerLabels.For(SourceKind.Remote, 0));
        Assert.Equal("Local Speaker 3", DefaultSpeakerLabels.For(SourceKind.Local, 2));
    }

    [Fact]
    public void Rediarise_colliding_clusterKey_preserves_pin_and_remaps_fresh()
    {
        // A fresh run RESTARTS cluster ids at 0, so its "Remote:0" can be a DIFFERENT speaker
        // than the pinned seq 42 that still maps to the old "Remote:0" (=Alice). The fresh
        // colliding key must be remapped so the pin's assignment AND name survive verbatim.
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["42"] = "Remote:0" } },
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["42"] },  // seq 42 pinned
            Names = new Dictionary<string, string> { ["Remote:0"] = "Alice" },
            DiarisedSources = [SourceKind.Remote],
        };
        // Fresh run: reassigns pinned seq 42 (must be ignored) and reuses cluster 0 for a NEW seq 50.
        var commit = Commit(
            new Dictionary<string, string> { ["42"] = "Remote:1", ["50"] = "Remote:0" },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" });

        var merged = SpeakersMerge.Merge(existing, commit);

        // Pin's assignment AND name are preserved verbatim.
        Assert.Equal("Remote:0", merged.Assignments["Remote"]["42"]);
        Assert.Equal("Alice", merged.Names["Remote:0"]);
        // Fresh seq 50 was remapped OFF the pinned key so a different speaker cannot steal the pin.
        var seq50Key = merged.Assignments["Remote"]["50"];
        Assert.NotEqual("Remote:0", seq50Key);
        // The fresh label followed its key through the remap (still "Remote Speaker 1", new key).
        Assert.Equal("Remote Speaker 1", merged.Names[seq50Key]);
        // The pinned name "Alice" is bound to exactly one clusterKey (no accidental duplication).
        Assert.Equal(1, merged.Names.Count(kv => kv.Value == "Alice"));
    }

    [Fact]
    public void Merge_does_not_mutate_existing_in_place()
    {
        var innerAssignments = new Dictionary<string, string> { ["4"] = "Remote:0", ["7"] = "Remote:9" };
        var innerNames = new Dictionary<string, string> { ["Remote:0"] = "Alice", ["Remote:9"] = "Judge Wu" };
        var innerPinned = new List<string> { "7" };
        var existing = new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>> { ["Remote"] = innerAssignments },
            Names = innerNames,
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = innerPinned },
            DiarisedSources = [SourceKind.Remote],
        };
        var commit = Commit(
            new Dictionary<string, string> { ["4"] = "Remote:1" },
            new Dictionary<string, string> { ["Remote:1"] = "Remote Speaker 1" });

        var merged = SpeakersMerge.Merge(existing, commit);

        // (a) the result's inner collections are fresh copies, not the caller's objects.
        Assert.NotSame(existing.Assignments["Remote"], merged.Assignments["Remote"]);
        Assert.NotSame(existing.Names, merged.Names);
        Assert.NotSame(existing.Pinned, merged.Pinned);

        // (b) every inner value of `existing` is unchanged after the call.
        Assert.Equal("Remote:0", existing.Assignments["Remote"]["4"]);   // NOT reset to the fresh "Remote:1"
        Assert.Equal("Remote:9", existing.Assignments["Remote"]["7"]);
        Assert.Equal(2, existing.Assignments["Remote"].Count);
        Assert.Equal("Alice", existing.Names["Remote:0"]);               // stale non-pinned name NOT dropped
        Assert.Equal("Judge Wu", existing.Names["Remote:9"]);
        Assert.Equal(2, existing.Names.Count);
        Assert.Equal(["7"], existing.Pinned["Remote"]);
    }
}

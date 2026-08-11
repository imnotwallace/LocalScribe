// tests/LocalScribe.App.Tests/EditableSectionViewModelTests.cs
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class EditableSectionViewModelTests
{
    private static DisplayRow OneSegmentRow() => new()
    {
        DisplayName = "Them", StartMs = 15000, EndMs = 17000, Text = "First. Second.",
        Segments =
        [
            new RowSegment(3, TranscriptSource.Remote, 15000, 17000, "First. Second.", "First. Second.",
                false, false),
        ],
    };

    [Fact]
    public void BeginEdit_MaterializesChildSegments()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        Assert.Empty(vm.Segments);                    // nothing until edit
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        Assert.Single(vm.Segments);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public void SplitSegment_ReplacesWithTwoHalves_AndCollectsSplitEdit()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments[0], caret: 6);   // after "First."
        Assert.Equal(2, vm.Segments.Count);

        var splits = vm.CollectSplits();
        Assert.Single(splits);
        Assert.Equal(3, splits[0].Seq);
        Assert.Equal(2, splits[0].Parts.Count);
        Assert.True(splits[0].Parts[1].DerivedStart);
    }

    private static DisplayRow TwoSegmentRow() => new()
    {
        DisplayName = "Them", StartMs = 15000, EndMs = 19000, Text = "First. Second. Third.",
        Segments =
        [
            new RowSegment(3, TranscriptSource.Remote, 15000, 17000, "First. Second.", "First. Second.",
                false, false),
            new RowSegment(4, TranscriptSource.Remote, 17000, 19000, "Third.", "Third.",
                false, false),
        ],
    };

    [Fact]
    public void SplitOneSegment_LeavesSiblingCollectibleAsCorrection()
    {
        var vm = new EditableSectionViewModel(TwoSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        var seq3 = vm.Segments.Single(s => s.Seq == 3);
        vm.SplitSegment(seq3, caret: 6);   // after "First."

        var seq4 = vm.Segments.Single(s => s.Seq == 4);
        seq4.EditedText = "Third revised.";

        Assert.False(seq4.IsSplitChild);
        Assert.Equal(0, seq4.PartIndex);

        var corrections = vm.CollectCorrections();
        Assert.True(corrections.ContainsKey(4));
        Assert.Equal("Third revised.", corrections[4]);
    }

    // Bug fix (final review): same speaker, two machine segments in one section with a 500 ms
    // silence gap between A's own EndMs (10800) and B's StartMs (11300) - SectionGrouper merges
    // same-speaker turns within sectionGapMs (5000 ms), so this shape is normal, not contrived.
    private static DisplayRow GappedTwoSegmentRow() => new()
    {
        DisplayName = "Them", StartMs = 10000, EndMs = 12000, Text = "First is quite long here. Second.",
        Segments =
        [
            new RowSegment(5, TranscriptSource.Remote, 10000, 10800,
                "First is quite long here.", "First is quite long here.", false, false),
            new RowSegment(6, TranscriptSource.Remote, 11300, 12000, "Second.", "Second.", false, false),
        ],
    };

    [Fact]
    public void SplitSegment_ClampsDerivedStart_ToOwnSegmentEndMs_NotNextSegmentStart()
    {
        var vm = new EditableSectionViewModel(GappedTwoSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        var seqA = vm.Segments.Single(s => s.Seq == 5);

        // "First is quite long here." is 25 chars; caret=20 is proportion 0.8. Clamped (correctly)
        // to A's own EndMs (10800) that lands at 10640 - well inside (10000, 10800]. Clamped
        // (wrongly, pre-fix) to B's StartMs (11300) that lands at 11040 - past A's own EndMs, which
        // is exactly the value EditStore.ApplySplitAsync would reject as outside (line.StartMs, line.EndMs].
        vm.SplitSegment(seqA, caret: 20);
        Assert.Equal(2, vm.Segments.Count(s => s.Seq == 5));

        // Part 0 (machine floor) stays read-only-estimate-free; part 1 is the derived estimate.
        var partsInOrder = vm.Segments.Where(s => s.Seq == 5).OrderBy(s => s.PartIndex).ToList();
        Assert.False(partsInOrder[0].DerivedStart);
        Assert.True(partsInOrder[1].DerivedStart);

        var splits = vm.CollectSplits();
        var splitA = splits.Single(s => s.Seq == 5);
        Assert.Equal(2, splitA.Parts.Count);
        Assert.All(splitA.Parts, p => Assert.InRange(p.StartMs, 10000, 10800));
        Assert.True(splitA.Parts[1].StartMs <= 10800,
            $"derived start {splitA.Parts[1].StartMs} must be clamped to seq 5's own EndMs (10800), " +
            "not seq 6's StartMs (11300).");
    }

    [Fact]
    public void RefreshSpeakerChoices_keeps_a_selection_across_a_rename_matched_by_id()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());   // seq 3, Remote
        var jane = new SpeakerChoice("Jane", "p-jane", null);
        vm.BeginEdit("relative", DateTimeOffset.UtcNow,
            remoteChoices: new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true), jane },
            localChoices: []);
        vm.Segments[0].Speaker = jane;

        // Session Details renamed p-jane "Jane" -> "Janet": same id, new display.
        vm.RefreshSpeakerChoices(
            new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true),
                new("Janet", "p-jane", null) }, []);

        var sel = vm.Segments[0].Speaker!;
        Assert.Equal("p-jane", sel.ParticipantId);               // preserved by id, not blanked
        Assert.Equal("Janet", sel.Display);                      // now shows the new name
    }

    [Fact]
    public void RefreshSpeakerChoices_falls_back_to_automatic_when_the_participant_is_removed()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        var jane = new SpeakerChoice("Jane", "p-jane", null);
        vm.BeginEdit("relative", DateTimeOffset.UtcNow,
            remoteChoices: new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true), jane },
            localChoices: []);
        vm.Segments[0].Speaker = jane;

        vm.RefreshSpeakerChoices(
            new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true) }, []);   // p-jane removed

        Assert.True(vm.Segments[0].Speaker!.IsUnassign);         // visible Automatic, not a blank/null
    }

    // Regression (2026-08-02 gold-edit smoke): re-entering Edit mode on an ALREADY-split segment
    // must pre-select each part's persisted speaker, not blank it. The old BeginEdit set split
    // children's Speaker to null, so a subsequent Save wrote null via CollectSplits and WIPED the
    // per-part speaker labels the user had painstakingly assigned.
    private static DisplayRow PersistedSplitWithPartSpeakers() => new()
    {
        DisplayName = "Them", StartMs = 15000, EndMs = 17000, Text = "First. Second.",
        Segments =
        [
            new RowSegment(3, TranscriptSource.Remote, 15000, 15800, "First.", "First. Second.",
                false, true, IsSplitChild: true, PartIndex: 0, SpeakerParticipantId: "p-alice"),
            new RowSegment(3, TranscriptSource.Remote, 15800, 17000, "Second.", "First. Second.",
                false, true, IsSplitChild: true, PartIndex: 1, SpeakerParticipantId: "p-bob"),
        ],
    };

    [Fact]
    public void BeginEdit_preselects_split_child_speakers_from_persisted_parts_and_roundtrips()
    {
        var vm = new EditableSectionViewModel(PersistedSplitWithPartSpeakers());
        var choices = new List<SpeakerChoice>
        {
            new("Automatic (Me / Them)", null, null, true),
            new("Alice", "p-alice", null),
            new("Bob", "p-bob", null),
        };
        vm.BeginEdit("relative", DateTimeOffset.UtcNow, remoteChoices: choices, localChoices: []);

        var parts = vm.Segments.Where(s => s.Seq == 3).OrderBy(s => s.PartIndex).ToList();
        Assert.Equal("p-alice", parts[0].Speaker?.ParticipantId);   // was null (wiped) before the fix
        Assert.Equal("p-bob", parts[1].Speaker?.ParticipantId);

        // And an untouched Save round-trips them, never nulling the persisted labels.
        var split = vm.CollectSplits().Single(s => s.Seq == 3);
        Assert.Equal("p-alice", split.Parts[0].SpeakerParticipantId);
        Assert.Equal("p-bob", split.Parts[1].SpeakerParticipantId);
    }

    [Fact]
    public void SplitThenRevert_LeavesSegmentCollectibleAsCorrection()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments[0], caret: 6);   // after "First."
        vm.RevertSplit(3);

        Assert.Single(vm.Segments);
        var survivor = vm.Segments[0];
        Assert.False(survivor.IsSplitChild);

        survivor.EditedText = "First. Second. Revised.";

        var corrections = vm.CollectCorrections();
        Assert.True(corrections.ContainsKey(3));
        Assert.Equal("First. Second. Revised.", corrections[3]);
    }

    // Regression (GUI smoke): reverting a split must RESTORE the single machine segment - full
    // original text and machine start/end - not leave the surviving part-0 fragment ("First.")
    // with the derived boundary as its end. The old code deleted PartIndex>0 parts only, so the
    // row showed the truncated fragment and the other part's text vanished.
    [Fact]
    public void RevertSplit_RestoresOriginalMachineSegment_TextAndBounds()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());   // seq 3 "First. Second." 15000..17000
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments[0], caret: 6);                // "First." | " Second."
        Assert.Equal(2, vm.Segments.Count);

        vm.RevertSplit(3);

        Assert.Single(vm.Segments);
        var survivor = vm.Segments[0];
        Assert.Equal("First. Second.", survivor.EditedText);      // restored, NOT the "First." fragment
        Assert.Equal("First. Second.", survivor.ProjectedText);
        Assert.False(survivor.IsSplitChild);
        Assert.Equal(0, survivor.PartIndex);
        Assert.Equal(15000, survivor.StartMs);                    // machine start
        Assert.Equal(17000, survivor.EndMs);                      // machine end, not the derived boundary
        Assert.Empty(vm.CollectSplits());                         // nothing to persist as a split
        Assert.Contains(3, vm.CollectSplitReverts());             // revert is queued
        Assert.False(vm.CollectCorrections().ContainsKey(3));     // restored text == projected: no phantom correction
    }

    [Fact]
    public void RevertSplit_InMultiSegmentSection_LeavesSiblingUntouched()
    {
        var vm = new EditableSectionViewModel(TwoSegmentRow());   // seq3 15000..17000, seq4 "Third." 17000..19000
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments.Single(s => s.Seq == 3), caret: 6);
        Assert.Equal(3, vm.Segments.Count);                       // seq3 x2 + seq4

        vm.RevertSplit(3);

        Assert.Equal(2, vm.Segments.Count);                       // seq3 restored + seq4
        var s3 = vm.Segments.Single(s => s.Seq == 3);
        Assert.Equal("First. Second.", s3.EditedText);
        Assert.Equal(17000, s3.EndMs);
        var s4 = vm.Segments.Single(s => s.Seq == 4);
        Assert.Equal("Third.", s4.EditedText);                    // sibling untouched
        Assert.False(s4.IsSplitChild);
        Assert.Equal(0, s4.PartIndex);
    }

    // ---- Task 8: human-authored (inserted) lines --------------------------------------------
    // Fixture helpers for the inserted-line tests below. MachineSeg/InsertedSeg/SplitChild build
    // RowSegments; RowWith wraps them in a DisplayRow; EditingSection builds a VM and immediately
    // calls BeginEdit (the shape every one of these tests wants). Note: the brief's own
    // "MachineSeg(seq: 0, ...)" call is a named argument followed by a positional one, which is
    // not legal C# (CS1738) - adapted here to the equivalent positional form.
    private static RowSegment MachineSeg(int seq, string text, long startMs = 0, long endMs = 1000)
        => new(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false);

    private static RowSegment InsertedSeg(string? insertionId, long startMs, string text,
        string? participantId = null)
        => new(0, TranscriptSource.System, startMs, startMs, text, text, false, false,
            SpeakerParticipantId: participantId, InsertionId: insertionId);

    private static RowSegment SplitChild(int seq, int partIndex, string text)
    {
        long startMs = partIndex * 100;
        return new RowSegment(seq, TranscriptSource.Remote, startMs, startMs + 100, text, text,
            false, false, IsSplitChild: true, PartIndex: partIndex);
    }

    private static DisplayRow RowWith(params RowSegment[] segments) => new() { Segments = segments };

    private static EditableSectionViewModel EditingSection(params RowSegment[] segments)
    {
        var section = new EditableSectionViewModel(RowWith(segments));
        section.BeginEdit("relative", DateTimeOffset.UnixEpoch);
        return section;
    }

    private static SpeakerChoice Unassign() => new("Automatic (Me / Them)", null, null, IsUnassign: true);

    private static EditableSectionViewModel EditingSectionWithChoices(
        IReadOnlyList<SpeakerChoice> remote, IReadOnlyList<SpeakerChoice> local, params RowSegment[] segments)
    {
        var section = new EditableSectionViewModel(RowWith(segments));
        section.BeginEdit("relative", DateTimeOffset.UnixEpoch, remoteChoices: remote, localChoices: local);
        return section;
    }

    [Fact]
    public void BeginEdit_materializes_an_inserted_segment_as_inserted()
    {
        var section = new EditableSectionViewModel(RowWith(
            MachineSeg(0, "Machine text."),
            InsertedSeg("i000001", 50_000, "Human text.")));
        section.BeginEdit("relative", DateTimeOffset.UnixEpoch);

        var inserted = Assert.Single(section.Segments, s => s.IsInserted);
        Assert.Equal("i000001", inserted.InsertionId);
        Assert.True(inserted.DerivedStart);   // a human estimate, shown as "(estimated)"
        Assert.Equal("Human text.", inserted.EditedText);
    }

    [Fact]
    public void An_inserted_segment_is_never_collected_as_a_correction()
    {
        // Machine seq starts at 0 and an inserted segment's placeholder Seq is 0. The brief's own
        // fixture here (a machine seg AND an inserted seg sharing "seq 0" in the SAME section) does
        // NOT falsify the guard: CollectCorrections' existing Count()==1 gate already excludes a
        // 2-member group regardless of which predicate produced it, so removing just the IsMachine
        // filter leaves this shape green (verified: Step 7 measured it staying green). The real
        // hazard is a section holding ONLY the inserted line - its placeholder "seq 0" group then
        // has exactly ONE member, passes Count()==1 on its own, and an unguarded GroupBy would
        // emit a phantom "correction" for machine seq 0 (wherever that real segment actually lives,
        // possibly a completely different section) carrying the inserted text.
        var section = EditingSection(InsertedSeg("i000001", 50_000, "Human text."));
        section.Segments.Single(s => s.IsInserted).EditedText = "Human text, edited.";

        Assert.Empty(section.CollectCorrections());
    }

    [Fact]
    public void An_inserted_segment_is_never_folded_into_a_machine_split()
    {
        // The brief's own fixture here (an UNSPLIT machine seg alongside an inserted seg) does NOT
        // falsify the guard: an inserted segment's IsSplitChild is always false, so
        // g.Any(s => s.IsSplitChild) excludes the seq-0 group either way (verified: measured it
        // staying green with the guard removed). The real hazard needs a GENUINE split at seq 0
        // sharing the group with an inserted line's placeholder seq 0 - an unguarded CollectSplits
        // would then fold the inserted line's text in as a spurious third "part" of that split.
        var section = EditingSection(SplitChild(0, 0, "First."), SplitChild(0, 1, "Second."),
            InsertedSeg("i000001", 50_000, "Human."));

        var split = Assert.Single(section.CollectSplits());
        Assert.Equal(2, split.Parts.Count);
    }

    [Fact]
    public void Editing_an_inserted_segment_is_collected_as_an_insertion_update()
    {
        var section = EditingSection(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human."));
        var seg = section.Segments.Single(s => s.IsInserted);
        seg.EditedText = "Human, corrected.";
        seg.StartMs = 51_000;

        var edit = Assert.Single(section.CollectInsertions());
        Assert.Equal("i000001", edit.InsertionId);
        Assert.Equal("Human, corrected.", edit.Text);
        Assert.Equal(51_000, edit.StartMs);
    }

    [Fact]
    public void An_untouched_inserted_segment_is_not_collected()
    {
        var section = EditingSection(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human."));
        Assert.Empty(section.CollectInsertions());
    }

    [Fact]
    public void AddInsertedLine_appends_a_pending_segment_with_no_id()
    {
        var section = EditingSection(MachineSeg(0, "Machine."));
        section.AddInsertedLine(startMs: 5_000);
        section.Segments[^1].EditedText = "Newly authored.";

        var edit = Assert.Single(section.CollectInsertions());
        Assert.Null(edit.InsertionId);      // null means MINT - EditStore allocates under the gate
        Assert.Equal("Newly authored.", edit.Text);
        Assert.Equal(5_000, edit.StartMs);
    }

    [Fact]
    public void A_pending_inserted_line_left_blank_is_not_collected()
    {
        var section = EditingSection(MachineSeg(0, "Machine."));
        section.AddInsertedLine(5_000);
        Assert.Empty(section.CollectInsertions());
    }

    [Fact]
    public void RemoveInsertedLine_reverts_a_persisted_line_and_just_drops_a_pending_one()
    {
        var section = EditingSection(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human."));
        section.AddInsertedLine(5_000);
        section.Segments[^1].EditedText = "Pending.";

        section.RemoveInsertedLine(section.Segments.Single(s => s.InsertionId == "i000001"));
        section.RemoveInsertedLine(section.Segments.Single(s => s.IsInserted && s.InsertionId is null));

        Assert.Equal(["i000001"], section.CollectInsertionReverts());
        Assert.Empty(section.CollectInsertions());
        Assert.DoesNotContain(section.Segments, s => s.IsInserted);
    }

    [Fact]
    public void Reindex_never_relabels_an_inserted_segment_as_a_split_child()
    {
        // Two inserted segments both carry Seq 0. An unguarded Reindex would see a count of 2 for
        // "seq 0" and flag BOTH as split children of machine seq 0.
        var section = EditingSection(MachineSeg(0, "Machine."),
            InsertedSeg("i000001", 50_000, "A."), InsertedSeg("i000002", 51_000, "B."));
        var machine = section.Segments.Single(s => !s.IsInserted);
        section.SplitSegment(machine, caret: 4);

        // Reindex's reconstruction does not forward isInserted, so an unguarded run wipes the flag
        // on both formerly-inserted segments entirely - the Assert.All below would then pass
        // VACUOUSLY (empty Where(IsInserted)). This assertion is the one that actually carries the
        // test's name.
        Assert.Equal(2, section.Segments.Count(s => s.IsInserted));
        Assert.All(section.Segments.Where(s => s.IsInserted), s =>
        {
            Assert.False(s.IsSplitChild);
            Assert.Equal(0, s.PartIndex);
        });
        Assert.Equal(2, section.CollectSplits().Single().Parts.Count);
    }

    [Fact]
    public void SplitSegment_refuses_an_inserted_segment()
    {
        var section = EditingSection(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human."));
        var inserted = section.Segments.Single(s => s.IsInserted);
        int before = section.Segments.Count;

        section.SplitSegment(inserted, caret: 3);

        Assert.Equal(before, section.Segments.Count);
        Assert.Empty(section.CollectSplits());
    }

    [Fact]
    public void RevertSplit_leaves_inserted_segments_in_the_section_untouched()
    {
        var section = EditingSection(SplitChild(0, 0, "First."), SplitChild(0, 1, "Second."),
            InsertedSeg("i000001", 50_000, "Human."));
        section.RevertSplit(seq: 0);

        Assert.Contains(section.Segments, s => s.InsertionId == "i000001");
    }

    [Fact]
    public void An_inserted_segment_preselects_its_participant_by_target()
    {
        var jane = new SpeakerChoice("Jane Okafor", "p-1", null);
        var section = new EditableSectionViewModel(RowWith(
            InsertedSeg("i000001", 50_000, "Human.", participantId: "p-1")));
        section.BeginEdit("relative", DateTimeOffset.UnixEpoch, remoteChoices: [jane], localChoices: [jane]);

        Assert.Same(jane, section.Segments.Single(s => s.IsInserted).Speaker);
    }

    // ---- Task 8 review amendment (2026-08-11): an inserted line has no side --------------------

    [Fact]
    public void An_inserted_segment_attributed_to_a_LOCAL_participant_is_preselected_and_survives()
    {
        // Defect A: routing TranscriptSource.System through ChoicesFor yields the REMOTE list only,
        // so this line would blank and the next CollectInsertions would write its speaker away.
        var jane = new SpeakerChoice("Jane Okafor", "p-local", null);
        var section = new EditableSectionViewModel(RowWith(
            InsertedSeg("i000001", 50_000, "Human.", participantId: "p-local")));
        section.BeginEdit("relative", DateTimeOffset.UnixEpoch,
            remoteChoices: [new SpeakerChoice("Automatic (Me / Them)", null, null, IsUnassign: true)],
            localChoices: [jane]);

        var seg = section.Segments.Single(s => s.IsInserted);
        Assert.Same(jane, seg.Speaker);

        seg.EditedText = "Human, edited.";
        Assert.Equal("p-local", Assert.Single(section.CollectInsertions()).SpeakerParticipantId);
    }

    [Fact]
    public void The_inserted_choice_list_offers_both_sides_with_ONE_no_override_entry()
    {
        var section = EditingSectionWithChoices(
            remote: [Unassign(), new SpeakerChoice("Remy", "p-remote", null)],
            local: [Unassign(), new SpeakerChoice("Lena", "p-local", null)],
            InsertedSeg("i000001", 50_000, "Human."));

        var choices = section.Segments.Single(s => s.IsInserted).SpeakerChoices;
        Assert.Single(choices, c => c.IsUnassign);
        Assert.Contains(choices, c => c.ParticipantId == "p-remote");
        Assert.Contains(choices, c => c.ParticipantId == "p-local");
    }

    [Fact]
    public void A_speaker_only_change_on_a_persisted_inserted_line_is_collected()
    {
        // Defect B: without the speaker leg this returns empty, and after Task 9 guards the pin loop
        // the re-attribution is dropped silently and permanently.
        var lena = new SpeakerChoice("Lena", "p-local", null);
        var section = EditingSectionWithChoices(remote: [Unassign()], local: [Unassign(), lena],
            InsertedSeg("i000001", 50_000, "Human.", participantId: null));

        section.Segments.Single(s => s.IsInserted).Speaker = lena;

        var edit = Assert.Single(section.CollectInsertions());
        Assert.Equal("i000001", edit.InsertionId);
        Assert.Equal("p-local", edit.SpeakerParticipantId);
        Assert.Equal("Human.", edit.Text);   // text unchanged - the speaker alone drove the collect
    }

    [Fact]
    public void RefreshSpeakerChoices_never_unassigns_a_locally_attributed_inserted_line()
    {
        var lena = new SpeakerChoice("Lena", "p-local", null);
        var section = EditingSectionWithChoices(remote: [Unassign()], local: [Unassign(), lena],
            InsertedSeg("i000001", 50_000, "Human.", participantId: "p-local"));

        section.RefreshSpeakerChoices([Unassign()], [Unassign(), lena with { Display = "Lena Novak" }]);

        var seg = section.Segments.Single(s => s.IsInserted);
        Assert.Equal("p-local", seg.Speaker?.ParticipantId);   // a RENAME keeps the target
        Assert.False(seg.Speaker?.IsUnassign);
    }

    // ---- Amendment (pre-Task 10, 2026-08-11): one speaker contract for an inserted line ---------
    // speakers.json maps diarisation CLUSTERS to names; a human-authored line has no cluster, so its
    // speaker is a meta.json participant id or nothing. Offering a cluster choice here lets the user
    // pick a target CollectInsertions cannot store (silently saves as Unattributed), and on a line
    // that was already Unattributed the null-vs-null change-detection disjunct then drops the edit
    // entirely - a no-op that looks like it worked. This is the SAME contract Task 10's dialog uses.

    [Fact]
    public void The_inserted_choice_list_never_offers_a_detected_voice_cluster()
    {
        var section = EditingSectionWithChoices(
            remote: [Unassign(), new SpeakerChoice("Remy", "p-remote", null),
                     new SpeakerChoice("Voice 2 (detected voice)", null, "Remote:2")],
            local: [Unassign()],
            InsertedSeg("i000001", 50_000, "Human."));

        var choices = section.Segments.Single(s => s.IsInserted).SpeakerChoices;
        Assert.DoesNotContain(choices, c => c.ClusterKey is not null);
        Assert.Contains(choices, c => c.ParticipantId == "p-remote");
    }

    [Fact]
    public void A_machine_segment_is_still_offered_detected_voice_clusters()
    {
        // The filter is for inserted rows ONLY - a split child still needs its cluster override.
        var section = EditingSectionWithChoices(
            remote: [Unassign(), new SpeakerChoice("Voice 2 (detected voice)", null, "Remote:2")],
            local: [Unassign()],
            MachineSeg(0, "Machine."));

        var choices = section.Segments.Single(s => !s.IsInserted).SpeakerChoices;
        Assert.Contains(choices, c => c.ClusterKey == "Remote:2");
    }
}

public class ReadSegmentTests
{
    [Fact]
    public void ReadSegment_marks_an_inserted_start_as_estimated()
    {
        var seg = new ReadSegment(new RowSegment(0, TranscriptSource.System, 50_000, 50_000,
            "Human.", "Human.", false, false, InsertionId: "i000001"));
        Assert.True(seg.IsEstimatedStart);
    }
}

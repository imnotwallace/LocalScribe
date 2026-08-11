# Next session prompt — Part 2: authoring a transcript line

Paste everything below the line into a fresh Claude Code session in `F:\LocalScribe`.

---

Implement **Part 2 (human-authored transcript lines)** of
`docs/superpowers/specs/2026-08-11-import-resilience-line-insertion-lean-export-design.md` (§2).

Parts 1 and 3 are both merged. Part 2 is the last of the three and has no successor.

Use `superpowers:writing-plans` to produce the plan, then `superpowers:subagent-driven-development`
to execute it. Do not re-brainstorm — every product decision below is already settled by the owner.
**Verify my anchors before trusting them.** I checked all of them against `a95fe04` myself, but line
numbers drift, and the spec's own anchors were written before Parts 1 and 3 landed — I found five
places where the spec is now wrong. Those are listed below and they override the spec.

## What Part 2 does

Let a person author a transcript line where the machine heard nothing — an inaudible passage, a
speaker off-mic, a line the ASR dropped entirely. Today this is not a disabled button; it is
designed out at four independent layers (spec §2.1), and the owner is blocked on it.

Storage is a **third overlay in `edits.json`**, keyed by its own generated id:

```
edits.json
  corrections : seq -> Correction      (existing)
  splits      : seq -> SplitEntry      (existing)
  insertions  : id  -> InsertedLine    (new)

InsertedLine { StartMs, Text, SpeakerRef, AddedAtUtc }
```

`transcript.jsonl` is **not** touched. Rendering happens in `TranscriptProjection.Build`, which is
the single choke point: one change lights up read view, edit view, `.docx`/`.md`/`.txt`,
`session.txt`, search, the semantic index, the MCP corpus and assistant citations at once.

## Owner decisions — settled, do not re-litigate

- **Disclosure:** the line reads plainly in the exported document; `manifest.json` carries the
  provenance. Consistent with the owner's standing position that human involvement is disclosed by
  the general disclaimer, not per-turn marks. (Part 3 deleted the per-turn `[text corrected]` mark
  outright — do not reintroduce a per-turn mark of any kind for insertions.)
- **Interaction:** both a right-click entry point in read view and an inline `+` affordance in edit
  mode.
- **No sentinel seq.** No `-1`, no reserved high range. A magic seq would flow into the sort key,
  the speakers key space and the citation format, and would be wrong in all three.
- **Inserted lines never enter `speakers.json`.** The speaker lives on the `InsertedLine` record
  itself (`SpeakerRef`). `speakers.json` maps *diarisation clusters* to names, and a human-authored
  line has no cluster.
- **Timestamp is human-estimated by definition.** It reuses the existing `SplitPart.DerivedStart`
  treatment and shows as `(estimated)` wherever a derived start already does. It must fall within
  the session duration; it need not fall inside any segment.
- **Removal is a revert, not a deletion.** Same shape as `RemoveSplitAsync`. The no-deletion rule
  protects machine output; it does not oblige the owner to keep a typo forever.
- **Write path is `MaintenanceService.SaveTranscriptEditsAsync`**, like every other overlay write,
  so the per-session gate holds and `RegenerateProjectionsAsync` reseals `manifest.json` exactly
  once.

## Verified against `a95fe04` — trust these over the spec where they disagree

Every anchor below I opened myself. The spec's §2 was written before Parts 1 and 3 merged.

**1. The count arithmetic has SIX sites, not five.** The spec lists five places where a third
`TranscriptKind` would break `SegmentCount + MarkerCount`. Part 1 added a sixth — the salvage path.
Current, verified:

| File | Lines |
|---|---|
| `SessionWriter.cs` | 151-152 |
| `OfflinePipelineRunner.cs` | 228-229 |
| `SessionController.cs` | 1888-1889 |
| `AudioImporter.cs` | 322-323 |
| `AudioImporter.cs` | **521-522 (added by Part 1, absent from the spec)** |
| `SpeakerDetectionStep.cs` | 196 (`MarkerCount` only) |

This strengthens rather than weakens the spec's argument for the overlay: the reason not to add a
third `TranscriptKind` just got one site worse.

**2. `TranscriptEditBatch` is in the App layer, not Core.** It is
`src/LocalScribe.App/Services/TranscriptEditBatch.cs:20-24` — four legs (`Corrections`,
`CorrectionReverts`, `Splits`, `SplitReverts`), with `SplitEdit` and `SplitPartEdit` above it in the
same file. The spec cites a bare filename and you will look in `Core/Model` first and not find it.

**3. The citation problem the spec flags does not exist.** §2.3 says *"`TranscriptCitation` must
render an inserted line without a seq. The plan picks the surface form; it must round-trip through
the assistant's citation validator."* Both halves are already solved:
- `TranscriptCitation.Format(DisplayRow row, string sessionTitle, DateTimeOffset startedAtLocal,
  string versionId)` takes a **`DisplayRow`** and its output shape is
  `"<text>" - <speaker>, <HH:MM:SS>, <title> of <yyyy-MM-dd> (transcript v<n>)`. **No seq appears
  anywhere in it.**
- The assistant side is timestamp-based too: `AssistantCitationFormat` matches
  `\[(\d{1,3}(?::\d{1,2}){1,2})\]` and `CitationValidator` works on those stamps.

So an inserted line carrying a timestamp round-trips with **no new surface form to invent**. Do not
spend a task on this. Do add a test proving it, since nothing currently pins it.

**4. A dedicated `SourceRank` needs no new `TranscriptSource` value — and must not get one.**
`PreRow` (`src/LocalScribe.Core/Projection/PreRow.cs:8-10`) is
`PreRow(long StartMs, long EndMs, int SourceRank, int Seq, string? Name, string Text, bool IsMarker,
RowSegment? Segment = null)` — `SourceRank` is a **plain int set at construction**, not derived at
sort time. `TranscriptProjection.Rank(TranscriptSource)` returns `Local => 0, Remote => 1, _ => 2`
and is called when building the `PreRow`. So an inserted row simply takes its own rank integer.
**Do not add a `TranscriptSource` enum value**: `LocalScribeJson.cs:30` registers
`JsonStringEnumConverter<TranscriptSource>` with no unmapped fallback, and
`TranscriptStore.cs:42-45` counts an unparseable line as `malformed++` and drops it — the exact
silent-disappearance failure the spec warns about for `TranscriptKind` applies identically here.

**5. Part 3 changed Part 2's export obligation, and left a trap in it.** The `Human edits` counts
line is gone. `HumanLayerCounts` survives, and it now feeds exactly two things:
`MetadataFormat.DisclaimerFor` (which picks between two disclaimer variants) and
`MetadataFormat.OmittedLine`. Part 2's whole export obligation is: **an inserted line must flip the
disclaimer to the "reviewed and corrected by a person" variant.**

The trap is what Part 3's own final review found the hard way. `DisclaimerFor`'s predicate reads
four counts, and one of them — `SpeakerNames` — had to be redefined mid-branch because
`speakers.json` `Names` holds *machine-generated* defaults that auto-detection writes with no user
involvement. A wholly automatic import was claiming human review in a document served on opposing
counsel. Read the doc comment on `HumanLayerCounts.SpeakerNames`
(`src/LocalScribe.Core/Projection/ExportProvenance.cs`) before you touch this — it records the rule
in full. **Whatever field you add for insertions, prove that only a human ever writes it.**

## Traps that will cost you a day each

1. **`speakers.json` and `edits.json` have different key spaces, and the spec's design depends on
   keeping them apart.** `SpeakerRef` on the `InsertedLine` is deliberately *not* a `clusterKey`.
   Resist the pull to route it through `NameResolver`'s cluster tiers.

2. **`TranscriptProjection.Build` has two overloads.** `:17` is the five-argument form that
   delegates to `:26`, the six-argument form with `out int suppressedSegmentCount`. An inserted line
   must never be counted as a suppressed segment — that count feeds the `Omitted:` export line,
   which is an anti-concealment disclosure about *machine* omissions.

3. **The reseal choke point is not universal.** `SpeakerDetectionStep.MarkAsync`
   (`src/LocalScribe.App/Services/SpeakerDetectionStep.cs:184-200`) is a production path that
   rewrites two sealed files and **deliberately** skips projection regeneration. Its own comment
   explains why. So "everything goes through `RegenerateProjectionsAsync`" is false in general —
   verify your write path explicitly rather than assuming.

4. **`EditStore`'s split fence is the model for validation, and it is strict.**
   `EditStore.cs:191-205` requires part 0 to inherit the machine start un-derived, every later part
   to be flagged `DerivedStart`, starts to strictly increase, and each to land in
   `(line.StartMs, line.EndMs]`. Insertions need the *opposite* freedom — any point in the session —
   so write the validation deliberately rather than by analogy.

5. **`EnsureSegmentAsync` / `EnsureSegmentsAsync` are the layer that currently makes insertion
   impossible** (`EditStore.cs:237-281`, throwing `"No transcript line with seq {seq}."`). Every
   existing write goes through one of them. The insertion path must bypass them **without**
   loosening them for corrections and splits.

6. **`RowSegment` is a positional record with defaults**
   (`src/LocalScribe.Core/Projection/RowSegment.cs:10-17`). A new `InsertionId` must be appended
   **after** `SpeakerClusterKey`, or every positional construction in ~20 test files silently
   rebinds arguments. `SearchLine` (`Search/SearchModels.cs:10-11`) is positional too.
   `McpTranscriptRowDto` (`Mcp/McpDtos.cs:28-29`) already nulls `Seq`/`PartIndex` for markers, so
   its shape genuinely is unchanged beyond the new field.

7. **The three raw-JSONL consumers must keep never seeing insertions.** All three verified to take
   `IReadOnlyList<TranscriptLine>` off `transcript.jsonl`, never `DisplayRow`:
   - `src/LocalScribe.Core/Diarisation/ClusterAssigner.cs:13`
   - `src/LocalScribe.Core/People/VoiceprintEnrollmentService.cs:182,208` — note **`People/`**, not
     `Voice/`, which is where the spec's wording sends you
   - `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs:210,394,416`

   An inserted line carries no voice evidence to cluster or enrol from. This is free if you keep
   insertions out of `transcript.jsonl`, but it needs a test that would fail if someone later
   "helpfully" merged them in.

8. **Read view's context menu has exactly six items today** (`ReadViewWindow.xaml:487-514`:
   Correct text, Reassign speaker, Reassign all of this speaker, Copy text, Copy with citation,
   Remove speaker pin). `Add line here…` joins them. Note the Stage-6 gotcha recorded in the repo:
   a `Style.Setter` `ContextMenu` never wires a `Click=` handler — route through the WindowProxy
   commands like the existing six.

## Decisions the plan must make explicitly

The spec does not settle these, and the Part 3 round showed that unstated choices become defects.

- **Where insertions enter the human-layer count.** A new `HumanLayerCounts.Insertions`, or folded
  into `Corrections`? A new field is more honest and matches the "each count maps to exactly one
  on-disk structure" rationale in that record's doc comment — but it means touching
  `DisclaimerFor`'s predicate, which Part 3 deliberately left alone as correct.
- **The inserted row's `SourceRank` value**, and therefore whether an inserted line sorts before or
  after a machine row at an identical `StartMs`. Pick one, justify it, and pin it with a test.
  `InsertionId` is the final ordinal tiebreak between two insertions sharing a timestamp.
- **Whether `manifest.json` gains an `insertedLines` disclosure.** The spec says yes, alongside
  `fabricatedSilence`, including its tri-state "known vs none" distinction
  (`SessionManifest.cs:49-50`: `bool FabricatedSilenceKnown` + `IReadOnlyList<FabricatedSpan>`).
  Decide whether that costs a manifest schema bump and what old builds do with it.
- **Whether the `(estimated)` marking appears in the EXPORTED document or only in the app.** Part 3
  cut the export header to seven lines on the principle that the reading copy is lean; a per-line
  `(estimated)` in the body is a different category from a header line, but it is the same argument
  the deleted `[text corrected]` mark lost.

## Blast radius — measured, not estimated

Test files referencing each surface, on `a95fe04`:

| Surface | Test files |
|---|---|
| `RowSegment` | 20 |
| `EditStore` | 17 |
| `TranscriptProjection` | 5 |
| `HumanLayerCounts` | 4 |
| `SearchLine` | 3 |
| `DisclaimerFor` | 3 |
| `TranscriptCitation` | 2 |
| `SaveTranscriptEdits` | 2 |
| `McpTranscriptRowDto` | 0 |

`RowSegment` and `EditStore` are where the cost is. `McpTranscriptRowDto` having zero direct test
references is worth knowing before you rely on the MCP layer being covered.

## Non-negotiables on this repo

- **Branch off `master` @ `a95fe04`; do not work on `master` directly.**
- Gate: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — **2610 tests, 0 failed**,
  ~70 s. Never `--no-build` from a stale tree.
- Also run the gate with `LOCALSCRIBE_MODELS` pointed at a fresh empty directory. Standing criterion
  since Part 1; CI fetches no models. Both gates were green at 2610 when Part 3 merged.
- **Demand absolute test numbers from every subagent — "N passed, M failed", never "0 new
  failures".** One Part 3 implementer reported a red gate the second way.
- Warning baseline: exactly **7** `CS8602`, all pre-existing in
  `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`. Verified on a clean `--no-incremental`
  rebuild. Any new warning is a defect.
- **ASCII only in `src/`** — `CONTRIBUTING.md:118`. There is **no automated byte-scan gate**; earlier
  handoffs claimed one and were wrong. It is a convention, enforced by review. (`ImplicitUsings` is
  enabled, so `using System.Linq;` is never required either.)
- British spelling in user-facing strings and docs.
- Don't run `LocalScribe.App.exe` while building — it locks `Core.dll` (MSB3027).
- Ignore `.claude/worktrees/` — two stale worktrees hold byte-identical copies of most files you
  will touch, and greps will surface them.
- Update `docs/specs/localscribe-specs.md` for anything you change that it documents. Part 3's plan
  named six passages and the implementer found four more; budget for the audit being incomplete.
- Known pre-existing flakes, unrelated: `ProcessAssistantHelperTests.Cancel_kills_the_stub_promptly`
  and `AssistantQaServiceTests.Dispose_racing_...`. Re-run in isolation; never "fix".

## Open items carried in from Parts 1 and 3

Not Part 2's to fix — flag if you touch the area.

- **Part 3's Word smoke is still outstanding.** Nothing automated proves the exported `.docx` opens;
  `OpenXmlValidator(Office2019)` is a schema check, not Word's corruption heuristic. Part 2 adds
  body rows rather than header structure, so it does not widen that risk — but the smoke covers both
  once run.
- **`master` is 12 commits ahead of `origin` and unpushed.**
- **`SpeakersMerge.cs:111-114`** re-keys a colliding cluster while carrying the old label verbatim,
  so `speakers.json` can legitimately hold `"Local:5" -> "Local Speaker 1"`, which
  `DefaultSpeakerLabels.IsMachineDefault` scores as human-named. Unreachable today (the remap only
  fires behind a real human act), untested, and the disclaimer now depends on that coupling.
- **`SpeakersMerge.ParseId:206-212`** parses the `"Source:id"` suffix with a bare `int.TryParse` —
  no `NumberStyles`, no `InvariantCulture` — the exact inconsistency the Part 3 fix guards against
  on its own path.
- **`Edits` carries only `Corrections` and `Splits`**, so "every human act reaches one of the
  disclaimer's counts" is unproven in general. Part 2 adds a third human act and is the natural
  moment to audit that claim properly.

Full rulings and the parked-findings list from Part 3:
`.superpowers/sdd/2026-08-11-part3-lean-export-header/progress.md` (git-ignored, kept deliberately —
those rulings exist nowhere in git history).

# Part 2 — Human-Authored Transcript Lines Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a person author a transcript line where the machine heard nothing, stored as a third
non-destructive overlay in `edits.json` and rendered through `TranscriptProjection.Build`, so it
reaches read view, edit view, every export format, search, the semantic index, the MCP corpus and
assistant citations at once.

**Architecture:** A new `insertions : id -> InsertedLine` map in `edits.json`. `transcript.jsonl` is
never touched, so `SegmentCount + MarkerCount` arithmetic (six sites) is untouched and no
`TranscriptKind` or `TranscriptSource` enum value is added. `TranscriptProjection.Build` merges
insertions into the `PreRow` stream before the existing sort; `RowSegment` gains a nullable
`InsertionId` that marks a row human-authored and declares `Seq`/`PartIndex`/`Source` meaningless.
Writes go through `MaintenanceService.SaveTranscriptEditsAsync` so the per-session gate holds and
`RegenerateProjectionsAsync` reseals `manifest.json` exactly once.

**Tech Stack:** .NET 8, C#, WPF (`Wpf.Ui`), CommunityToolkit.Mvvm, xUnit, System.Text.Json.

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Branch off `master` @ `a95fe04`.** Do not commit to `master`. Branch name:
  `feat/part2-line-insertion-2026-08-11`.
- **Gate:** `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — the baseline is
  **2610 passed, 0 failed**, ~70 s. Every task ends green. Never pass `--no-build` from a stale tree.
- **Second gate:** the same command with `LOCALSCRIBE_MODELS` pointed at a fresh empty directory.
  Standing criterion since Part 1; CI fetches no models. Run it at Task 12 and any time a task
  touches a model-resolving path (none should).
- **Report absolute test numbers.** "N passed, M failed" — never "no new failures".
- **Warning baseline: exactly 7 `CS8602`**, all pre-existing in
  `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`. Any new warning is a defect.
- **ASCII only in `src/`** (`CONTRIBUTING.md:118`). No em dashes, no smart quotes, no emoji in
  source. Use `\u` escapes where a non-ASCII character is genuinely required in a string literal
  (the `ExportNotices` precedent). There is **no automated byte-scan gate** — this is enforced by
  review. (`ExportNotices.cs`'s doc comment claims a byte-scan gate exists; it is stale. Do not
  "fix" it in this branch.)
- **`ImplicitUsings` is enabled.** Never add `using System.Linq;` or `using System.Collections.Generic;`.
- **British spelling in user-facing strings and docs** ("recognised", "summarise", "attributable").
  Existing identifiers keep their spelling.
- **Never `DateTime.UtcNow`.** All time comes from the injected `TimeProvider`.
- **Don't run `LocalScribe.App.exe` while building** — it locks `Core.dll` (MSB3027).
- **Ignore `.claude/worktrees/`.** Two stale worktrees hold byte-identical copies of most files in
  this plan; greps will surface them. Never edit a file under that path.
- **Known pre-existing flakes, unrelated to this work:**
  `ProcessAssistantHelperTests.Cancel_kills_the_stub_promptly` and
  `AssistantQaServiceTests.Dispose_racing_...`. Re-run in isolation. Never "fix" them.
- **Commit after every task.** Conventional commit prefixes (`feat:`, `fix:`, `test:`, `docs:`).

---

## Settled decisions (do not re-litigate)

These were open in the spec or the handoff. They are decided here; each has a task that pins it
with a test.

| Decision | Ruling | Where pinned |
|---|---|---|
| Human-layer count | **New `HumanLayerCounts.Insertions` field**, and `DisclaimerFor` gains `\|\| c.Insertions > 0`. Not folded into `Corrections`. | Task 4 |
| Inserted row `SourceRank` | **`-1`** — an inserted row sorts **before** a machine row at an identical `StartMs`. | Task 3 |
| Tiebreak between two insertions | `RowSegment.InsertionId` ordinal, read off `PreRow.Segment`. Ids are minted `i` + 6-digit zero-padded ordinal, so ordinal string order **is** creation order. | Task 3 |
| Id reuse after a revert | **Never.** `Edits.HighestInsertionOrdinal` is a persisted high-water mark; a reverted id is never handed to a different human act. See the amendment below. | Task 2 |
| `manifest.json` disclosure | **Yes**, root-level `InsertedLines`. **No schema bump** — stays `SchemaVersion = 1`. No tri-state. | Task 6 |
| `edits.json` schema version | **Max supported 2; written as 2 only when the insertions map is non-empty.** | Task 1 |
| `(estimated)` in the exported document | **No — app only.** It shows in read view and edit mode exactly where a split child's derived start already does. | Task 3, Task 8 |
| Inserted row `RowSegment.Source` | `TranscriptSource.System`. Meaningless, like `Seq`/`PartIndex`, but non-colliding. | Task 3 |
| `InsertedLine.SpeakerRef` | A **`meta.json` participant id**, or null. Never a `clusterKey`. Never routed through `NameResolver`'s cluster tiers. | Task 3 |
| Unattributed inserted line | Renders the fixed label `"Unattributed"`. `PreRow.Name` is never null for a non-marker row today and must not become null. | Task 3 |
| Inserted row `EndMs` | `== StartMs` (zero length). A human estimate is a point, not a measured span. | Task 3 |
| Removal entry point | **Edit mode only.** The read-view context menu gains one item (`Add line here...`), not two. | Task 11 |

### Why `SourceRank = -1` (insert-before)

Both entry points are positional gestures. `Add line here...` on the row at 05:00 seeds 05:00; the
edit-mode `+` sits below a section and seeds the boundary after it. "Insert at position N" means
"before what is currently at N" in every editor. Ranking insertions after machine rows would make a
line typed at the `+` jump below the following row, which reads as a bug. Nothing about machine
rows' order relative to each other changes: `-1` only decides an exact tie, and only against a
human addition.

### AMENDMENT (Task 2 review, 2026-08-11): ids must never be reused after a revert

**The defect this plan shipped into Task 2.** `MintId` was specified to mint one past the highest
id *currently in the map*, and `ApplyInsertionsAsync` applies reverts **before** writes. So a batch
that reverts `i000001` and writes a new line with `Id: null` empties the map first and then re-mints
`i000001` for unrelated text. The same reuse recurs across separate calls: revert the highest id in
one call, insert in the next, and the revoked id comes back. `InsertedLineLabels.MintId`'s own doc
comment forbids exactly this ("reusing a reverted id would make two different human acts share an
identity in the manifest disclosure") — the plan's prose was right and the plan's code defeated it.

Why it matters, concretely: `manifest.json` discloses inserted lines by id (Task 6) and the `.zip`
bundle is a verbatim folder copy. An export served on Monday can say `i000001 @ 01:01, added Monday`
and an export of the same session served on Tuesday can say `i000001 @ 05:30, added Tuesday` — two
different human acts wearing one identity across two documents in the other side's hands.

**The fix.** A persisted high-water mark on `Edits`, because a revert genuinely removes the entry —
`edits.json` has no tombstones by design (`Edits`'s own doc comment) — so the surviving keys cannot
carry the memory:

```csharp
    /// <summary>The highest insertion ordinal ever minted for this version, so a REVERTED id is
    /// never handed to a different human act (Task 2 review, 2026-08-11). Not derivable from
    /// Insertions: a revert removes the entry outright - this file has no tombstones by design -
    /// so minting from the surviving keys alone resurrects the revoked id, and manifest.json
    /// discloses inserted lines BY ID, so two exports of one session could carry two different
    /// human acts under one identity. Zero on a file written before insertions existed, which is
    /// correct: nothing has been minted. Additive and skippable, so it does NOT raise
    /// WriteVersion - a file with a high-water mark but no insertions has nothing an older build
    /// could drop.</summary>
    public int HighestInsertionOrdinal { get; init; }
```

`InsertedLineLabels.MintId` takes the mark and keeps the key-derived value as a self-healing floor:

```csharp
    /// <summary>Mint the next insertion id: "i" + a zero-padded ordinal one past the highest ever
    /// minted for this version. <paramref name="highestMinted"/> is the persisted high-water mark
    /// and is the authority; the existing keys are a FLOOR, so a hand-edited edits.json whose
    /// counter is missing or lower than its own keys still mints forward rather than colliding.
    /// [...keep the rest of the existing doc: zero-padding and the ordinal-sort tie, NumberStyles
    /// and InvariantCulture explicit, an unparseable key skipped rather than throwing...]</summary>
    public static string MintId(IEnumerable<string> existingIds, int highestMinted)
```

`ApplyInsertionsAsync` tracks the mark across the batch and writes it back:

```csharp
        int highest = edits.HighestInsertionOrdinal;
        foreach (var w in writes)
        {
            string id = w.Id ?? InsertedLineLabels.MintId(map.Keys, highest);
            if (w.Id is null) highest = InsertedLineLabels.OrdinalOf(id) ?? highest;
            map[id] = new InsertedLine { ... };
            changed = true;
        }
        ...
        await JsonFile.WriteAsync(EditsPath, edits with
        {
            SchemaVersion = WriteVersion(map), Insertions = map, HighestInsertionOrdinal = highest,
        }, ct);
```

`InsertedLineLabels.OrdinalOf(string id) -> int?` is the single parse helper `MintId` already needs
internally; expose it rather than parsing the id twice in two places.

**Tests this amendment requires** (replacing the brief's `MintId_starts_at_i000001_...` with the
two-argument form and adding three):

```csharp
[Fact]
public void MintId_takes_the_high_water_mark_over_the_surviving_keys()
{
    Assert.Equal("i000001", InsertedLineLabels.MintId([], 0));
    Assert.Equal("i000002", InsertedLineLabels.MintId(["i000001"], 1));
    // The mark wins when every key has been reverted away.
    Assert.Equal("i000004", InsertedLineLabels.MintId([], 3));
    // The keys are a self-healing FLOOR when the mark is stale or absent.
    Assert.Equal("i000006", InsertedLineLabels.MintId(["i000005"], 0));
    Assert.Equal("i000002", InsertedLineLabels.MintId(["i000001", "hand-edited"], 0));
}

[Fact]
public async Task Reverting_and_inserting_in_ONE_batch_never_reuses_the_revoked_id()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);

    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Different act.", 2_000, null)],
        ["i000001"], 600_000, default);

    var edits = await store.LoadAsync(default);
    var only = Assert.Single(edits!.Insertions);
    Assert.Equal("i000002", only.Key);
    Assert.Equal("Different act.", only.Value.Text);
}

[Fact]
public async Task Reverting_then_inserting_in_a_LATER_call_never_reuses_the_revoked_id()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);
    await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default);

    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Different act.", 2_000, null)], [], 600_000, default);

    Assert.Equal("i000002", Assert.Single((await store.LoadAsync(default))!.Insertions).Key);
}

[Fact]
public async Task The_high_water_mark_survives_a_file_that_drops_back_to_schema_version_1()
{
    // Reverting the only insertion returns the file to v1 (nothing an older build could drop),
    // but the mark must persist or the NEXT insertion reuses the revoked id.
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "First.", 1_000, null)], [], 600_000, default);
    await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default);

    var edits = await store.LoadAsync(default);
    Assert.Equal(1, edits!.SchemaVersion);
    Assert.Empty(edits.Insertions);
    Assert.Equal(1, edits.HighestInsertionOrdinal);
}
```

**Also settled by the same review** (both were Minor, both are one-liners with a test):

- **`AddedAtUtc` is preserved on an in-place update.** The field is named *Added*; overwriting it
  when a person merely retypes their own line makes the name a lie, and `manifest.json` discloses
  it as when the line was authored. `ApplyInsertionsAsync` carries the existing entry's
  `AddedAtUtc` forward when `w.Id` names one already in the map, and stamps `_time.GetUtcNow()`
  only for a genuinely new line. Test: update an existing insertion under an advanced clock and
  assert `AddedAtUtc` is unchanged while `Text` and `StartMs` are not.
- **A negative start is rejected even when the duration is unknown.** The guard is already
  unconditional in code; add the test that locks it in
  (`ApplyInsertionsAsync(..., sessionDurationMs: 0)` with `StartMs: -1` throws).

### Why `edits.json` writes version 2 only when insertions exist

`EditStore.LoadAsync` calls `SchemaGuard.RejectIfNewer(fileVersion, Version, "edits.json")`
(`EditStore.cs:261`). An older build has `Version = 1`.

- If a file containing insertions is stamped `1`, an older build reads it, **silently drops the
  insertions**, renders a transcript missing human-authored evidence, and — because
  `HumanLayerCounts.Insertions` would also be 0 — prints the "untouched" disclaimer over a document
  a person authored lines into. That is the exact silent-disappearance failure the spec rejects for
  a third `TranscriptKind`.
- If every file is stamped `2`, every session the new build touches becomes unreadable to an older
  build even when it has no insertions at all. Gratuitous.

Stamping on demand fails loud precisely when there is something to lose, and changes nothing
otherwise — so every existing edits.json byte-identity test stays green unchanged.

---

## File Structure

**Core — created**

| File | Responsibility |
|---|---|
| `src/LocalScribe.Core/Projection/InsertedLineLabels.cs` | The `"Unattributed"` fallback label and the id-minting/format helpers. One place, so Core and App cannot drift. |

**Core — modified**

| File | Change |
|---|---|
| `src/LocalScribe.Core/Model/Edits.cs` | `InsertedLine` record; `Edits.Insertions` map. |
| `src/LocalScribe.Core/Storage/EditStore.cs` | `Version` 1 -> 2 (max supported); `WriteVersion`; `ApplyInsertionsAsync`; `RemoveInsertionAsync`; `ReadInsertionsAsync` static; insertion validation. |
| `src/LocalScribe.Core/Projection/RowSegment.cs` | Trailing `string? InsertionId = null`. |
| `src/LocalScribe.Core/Projection/PreRow.cs` | Doc only — records that the sort's fourth key reads `Segment?.InsertionId`. |
| `src/LocalScribe.Core/Projection/TranscriptProjection.cs` | `InsertedRank`; merge insertions into `pre`; fourth sort key; pinned-lookup guard. |
| `src/LocalScribe.Core/Projection/NameResolver.cs` | `ResolveInserted(string? participantId, SessionMeta meta)`. |
| `src/LocalScribe.Core/Projection/ExportProvenance.cs` | `HumanLayerCounts.Insertions`. |
| `src/LocalScribe.Core/Projection/MetadataFormat.cs` | `DisclaimerFor` predicate gains the insertions leg. |
| `src/LocalScribe.Core/Search/SearchModels.cs` | `SearchLine` trailing `string? InsertionId = null`. |
| `src/LocalScribe.Core/Search/SearchIndexBuilder.cs` | Pass `seg.InsertionId`. |
| `src/LocalScribe.Core/Mcp/McpDtos.cs` | `McpTranscriptRowDto` trailing `string? InsertionId = null`. |
| `src/LocalScribe.Core/Mcp/McpCorpus.cs` | `ReadUnit.InsertionId`; seq-range and `aroundSeq` exclude inserted units. |
| `src/LocalScribe.Core/Model/SessionManifest.cs` | `InsertedLineRef` record; `SessionManifest.InsertedLines`. |
| `src/LocalScribe.Core/Storage/ManifestBuilder.cs` | Read the version's insertions and record them. |

**App — created**

| File | Responsibility |
|---|---|
| `src/LocalScribe.App/ViewModels/InsertLineViewModel.cs` | WPF-free editor VM for the `Add line here...` dialog: seeding, timestamp parse, validation, gated save. |
| `src/LocalScribe.App/InsertLineDialog.xaml` / `.xaml.cs` | Humble modal shell over that VM (the `CorrectTextDialog` pattern). |

**App — modified**

| File | Change |
|---|---|
| `src/LocalScribe.App/Services/TranscriptEditBatch.cs` | `InsertedLineEdit` record; two new legs on `TranscriptEditBatch`. |
| `src/LocalScribe.App/Services/MaintenanceService.cs` | `SaveTranscriptEditsAsync` handles the insertion legs; `ProvenanceFor` counts insertions. |
| `src/LocalScribe.App/ViewModels/ReadSegment.cs` | `IsEstimatedStart` true for an inserted segment. |
| `src/LocalScribe.App/ViewModels/EditableSegmentViewModel.cs` | `IsInserted`, `InsertionId`. |
| `src/LocalScribe.App/ViewModels/EditableSectionViewModel.cs` | Guard every seq-keyed grouping; `AddInsertedLine`; `RemoveInsertedLine`; `CollectInsertions`; `CollectInsertionReverts`. |
| `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs` | `CreateInsertLineEditor`; insertion legs in `SaveEditsAsync`; pin-loop guard; `HasUnsavedEdits` guard. |
| `src/LocalScribe.App/ReadViewWindow.xaml` | Seventh context-menu item; edit-mode `+` / remove affordances. |
| `src/LocalScribe.App/ReadViewWindow.xaml.cs` | `AddLineCommand` on the window (WindowProxy). |

**Docs — modified**

| File | Change |
|---|---|
| `docs/specs/localscribe-specs.md` | Every passage that describes `edits.json`, the human layer, the projection row model, or `manifest.json`. |

---

### Task 1: `edits.json` gains an insertions overlay

**Files:**
- Modify: `src/LocalScribe.Core/Model/Edits.cs`
- Modify: `src/LocalScribe.Core/Storage/EditStore.cs:10` (the `Version` const) and every
  `SchemaVersion = Version` write site (`:39`, `:96`, `:216`, `:230`)
- Test: `tests/LocalScribe.Core.Tests/EditStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `LocalScribe.Core.Model.InsertedLine` with `{ string Text, long StartMs, string?
  SpeakerParticipantId, DateTimeOffset AddedAtUtc }`; `Edits.Insertions` of type
  `IReadOnlyDictionary<string, InsertedLine>`; `EditStore.Version = 2`;
  `EditStore.WriteVersion(IReadOnlyDictionary<string, InsertedLine>) -> int`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/EditStoreTests.cs`. Follow the fixture pattern already used
in that file for creating a finalized session directory — read the top of the file first and reuse
its existing helper rather than writing a new one.

```csharp
[Fact]
public async Task Edits_roundtrip_carries_insertions()
{
    var edits = new Edits
    {
        SchemaVersion = 2,
        Insertions = new Dictionary<string, InsertedLine>
        {
            ["i000001"] = new InsertedLine
            {
                Text = "The witness said something off-mic here.",
                StartMs = 61_500,
                SpeakerParticipantId = "p-1",
                AddedAtUtc = DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
            },
        },
    };
    string path = Path.Combine(_dir, "roundtrip-edits.json");
    await JsonFile.WriteAsync(path, edits, default);
    var back = await JsonFile.ReadAsync<Edits>(path, default);

    Assert.NotNull(back);
    var line = Assert.Single(back!.Insertions).Value;
    Assert.Equal("The witness said something off-mic here.", line.Text);
    Assert.Equal(61_500, line.StartMs);
    Assert.Equal("p-1", line.SpeakerParticipantId);
}

[Fact]
public async Task Edits_default_has_an_empty_insertions_map_not_null()
{
    string path = Path.Combine(_dir, "legacy-edits.json");
    await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"corrections\":{}}");
    var back = await JsonFile.ReadAsync<Edits>(path, default);

    Assert.NotNull(back);
    Assert.Empty(back!.Insertions);
}

[Fact]
public void WriteVersion_is_1_without_insertions_and_2_with_them()
{
    Assert.Equal(1, EditStore.WriteVersion(new Dictionary<string, InsertedLine>()));
    Assert.Equal(2, EditStore.WriteVersion(new Dictionary<string, InsertedLine>
    {
        ["i000001"] = new InsertedLine { Text = "x", StartMs = 0 },
    }));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~EditStoreTests" -v q`
Expected: FAIL — `InsertedLine` and `EditStore.WriteVersion` do not exist (compile errors).

- [ ] **Step 3: Add the storage shape**

In `src/LocalScribe.Core/Model/Edits.cs`, add `InsertedLine` above `Edits`:

```csharp
/// <summary>One transcript line a PERSON authored where the machine produced nothing (design
/// 2026-08-11 section 2.3): an inaudible passage, a speaker off-mic, a turn the ASR dropped
/// entirely. Stored in edits.json keyed by its own generated id, NEVER in transcript.jsonl - the
/// machine record stays a pure record of what the machine produced, so SegmentCount + MarkerCount
/// arithmetic (six sites) and the TranscriptKind enum are both untouched.
///
/// StartMs is human-estimated BY DEFINITION and gets the same treatment as SplitPart.DerivedStart:
/// it must fall within the session duration but need NOT fall inside any machine segment, which is
/// the whole point - the machine found no speech there.
///
/// SpeakerParticipantId is a meta.json participant id, or null for an unattributed line. It is
/// DELIBERATELY NOT a clusterKey: speakers.json maps DIARISATION CLUSTERS to names, and a
/// human-authored line has no cluster, so routing this through NameResolver's cluster tiers would
/// be a category error and would collide two key spaces that must stay apart.</summary>
public sealed record InsertedLine
{
    public string Text { get; init; } = "";
    public long StartMs { get; init; }
    public string? SpeakerParticipantId { get; init; }
    public DateTimeOffset AddedAtUtc { get; init; }
}
```

Then add the member to `Edits` (after `Splits`, so the JSON property order stays stable for the two
existing maps):

```csharp
    /// <summary>Human-authored lines, keyed by generated id (design 2026-08-11 section 2.3). The
    /// THIRD human overlay, alongside Corrections and Splits. Present only when non-empty, which is
    /// also what gates the schema version - see EditStore.WriteVersion.</summary>
    public IReadOnlyDictionary<string, InsertedLine> Insertions { get; init; }
        = new Dictionary<string, InsertedLine>();
```

- [ ] **Step 4: Raise the supported schema version, on demand**

In `src/LocalScribe.Core/Storage/EditStore.cs`, replace the `Version` const and add `WriteVersion`:

```csharp
    /// <summary>The NEWEST edits.json this build can read. Raised to 2 by the insertions overlay
    /// (design 2026-08-11 section 2.3).</summary>
    public const int Version = 2;

    /// <summary>The version to STAMP on a write: 2 only when there is an insertions map to lose,
    /// 1 otherwise.
    ///
    /// A file stamped 1 that carries insertions is read by an older build, whose LoadAsync accepts
    /// it and then silently DROPS the insertions - it renders a transcript missing human-authored
    /// evidence and, because its HumanLayerCounts has no Insertions member, prints the "untouched"
    /// disclaimer over a document a person authored lines into. That is the exact silent
    /// disappearance the design rejects for a third TranscriptKind, and it must fail loud instead.
    ///
    /// Stamping 2 unconditionally would instead lock every older build out of every session this
    /// build ever touched, including the overwhelming majority with no insertions at all. Stamping
    /// on demand fails loud exactly when there is something to lose and changes nothing otherwise -
    /// so every existing edits.json byte-identity test stays green unchanged.</summary>
    public static int WriteVersion(IReadOnlyDictionary<string, InsertedLine> insertions)
        => insertions.Count > 0 ? 2 : 1;
```

Now update all four existing write sites so a correction/split write cannot silently downgrade an
edits.json that already holds insertions. In each of `ApplyTextCorrectionAsync` (`:39`),
`ApplyTextEditsAsync` (`:96`), `ApplySplitAsync` (`:215-216`) and `RemoveSplitAsync` (`:230`),
change `SchemaVersion = Version` to `SchemaVersion = WriteVersion(edits.Insertions)`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~EditStoreTests" -v q`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2613 passed, 0 failed** (2610 baseline + 3 new). Report the absolute numbers.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Model/Edits.cs src/LocalScribe.Core/Storage/EditStore.cs tests/LocalScribe.Core.Tests/EditStoreTests.cs
git commit -m "feat(edits): add the insertions overlay shape and on-demand schema version 2"
```

---

### Task 2: `EditStore` writes and reverts insertions

**Files:**
- Modify: `src/LocalScribe.Core/Storage/EditStore.cs`
- Create: `src/LocalScribe.Core/Projection/InsertedLineLabels.cs`
- Test: `tests/LocalScribe.Core.Tests/EditStoreTests.cs`

**Interfaces:**
- Consumes: `InsertedLine`, `Edits.Insertions`, `EditStore.WriteVersion` (Task 1).
- Produces:
  - `InsertedLineLabels.Unattributed` (`const string = "Unattributed"`)
  - `InsertedLineLabels.MintId(IEnumerable<string> existingIds) -> string`
  - `EditStore.ApplyInsertionsAsync(IReadOnlyList<InsertedLineWrite> writes, IReadOnlyCollection<string> reverts, long sessionDurationMs, CancellationToken) -> Task<bool>`
  - `EditStore.InsertedLineWrite` record: `(string? Id, string Text, long StartMs, string? SpeakerParticipantId)`
  - `EditStore.ReadInsertionsAsync(string editsPath, CancellationToken) -> Task<IReadOnlyDictionary<string, InsertedLine>>`

`Id == null` means "mint a new insertion"; a non-null `Id` updates that existing insertion in place.
Minting happens inside this method, under the caller's per-session gate, so two edit-mode sections
adding a line each can never collide on an id.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/EditStoreTests.cs`. Reuse the file's existing
finalized-session fixture helper; the sketch below names it `NewFinalizedSessionAsync` — rename to
whatever the file actually provides.

```csharp
[Fact]
public void MintId_starts_at_i000001_and_takes_the_max_plus_one()
{
    Assert.Equal("i000001", InsertedLineLabels.MintId([]));
    Assert.Equal("i000002", InsertedLineLabels.MintId(["i000001"]));
    // A gap left by a revert is not reused - the next id is past the HIGHEST, not the count.
    Assert.Equal("i000004", InsertedLineLabels.MintId(["i000001", "i000003"]));
    // An unparseable key never blocks minting.
    Assert.Equal("i000002", InsertedLineLabels.MintId(["i000001", "hand-edited"]));
}

[Fact]
public async Task ApplyInsertions_mints_ids_and_writes_schema_version_2()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    bool wrote = await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Off-mic answer.", 61_500, "p-1"),
         new EditStore.InsertedLineWrite(null, "Second one.", 62_000, null)],
        [], sessionDurationMs: 600_000, default);

    Assert.True(wrote);
    var edits = await store.LoadAsync(default);
    Assert.Equal(2, edits!.SchemaVersion);
    Assert.Equal(["i000001", "i000002"], edits.Insertions.Keys.OrderBy(k => k, StringComparer.Ordinal));
    Assert.Equal("Off-mic answer.", edits.Insertions["i000001"].Text);
    Assert.Equal("p-1", edits.Insertions["i000001"].SpeakerParticipantId);
    Assert.Null(edits.Insertions["i000002"].SpeakerParticipantId);
}

[Fact]
public async Task ApplyInsertions_updates_an_existing_id_in_place()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "First text.", 1_000, null)], [], 600_000, default);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite("i000001", "Corrected text.", 2_000, "p-2")], [], 600_000, default);

    var edits = await store.LoadAsync(default);
    var only = Assert.Single(edits!.Insertions);
    Assert.Equal("i000001", only.Key);
    Assert.Equal("Corrected text.", only.Value.Text);
    Assert.Equal(2_000, only.Value.StartMs);
    Assert.Equal("p-2", only.Value.SpeakerParticipantId);
}

[Fact]
public async Task ApplyInsertions_revert_removes_the_entry_and_drops_back_to_version_1()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Typo.", 1_000, null)], [], 600_000, default);

    bool wrote = await store.ApplyInsertionsAsync([], ["i000001"], 600_000, default);

    Assert.True(wrote);
    var edits = await store.LoadAsync(default);
    Assert.Empty(edits!.Insertions);
    // The file no longer carries anything an older build would drop, so it is readable again.
    Assert.Equal(1, edits.SchemaVersion);
}

[Fact]
public async Task ApplyInsertions_reverting_an_unknown_id_is_a_quiet_no_op()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    Assert.False(await store.ApplyInsertionsAsync([], ["i999999"], 600_000, default));
}

[Fact]
public async Task ApplyInsertions_rejects_blank_text()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "   ", 1_000, null)], [], 600_000, default));
}

[Fact]
public async Task ApplyInsertions_rejects_a_negative_start()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Text.", -1, null)], [], 600_000, default));
}

[Fact]
public async Task ApplyInsertions_rejects_a_start_past_the_session_duration()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Text.", 600_001, null)], [], 600_000, default));
}

[Fact]
public async Task ApplyInsertions_allows_any_start_when_the_duration_is_unknown()
{
    // A crash-recovered session can carry DurationMs 0 because finalize never computed one.
    // Refusing every insertion there would block exactly the evidentiary repair this feature is
    // for, so the upper bound is enforced only when it is KNOWN - the same "none is not unknown"
    // distinction ManifestFile.FabricatedSilenceKnown draws.
    var store = await NewFinalizedSessionAsync(durationMs: 0);
    Assert.True(await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Text.", 5_000_000, null)], [], 0, default));
}

[Fact]
public async Task ApplyInsertions_does_NOT_require_the_start_to_land_inside_a_segment()
{
    // The whole point: the machine heard nothing there. This must not inherit the split fence.
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);   // fixture segments are near 0
    Assert.True(await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Silence filled by a person.", 599_000, null)],
        [], 600_000, default));
}

[Fact]
public async Task Corrections_and_splits_still_reject_an_absent_seq()
{
    // The insertion path bypasses EnsureSegmentAsync/EnsureSegmentsAsync. It must not LOOSEN them.
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await Assert.ThrowsAsync<ArgumentException>(() =>
        store.ApplyTextCorrectionAsync(9_999, "nope", default));
    await Assert.ThrowsAsync<ArgumentException>(() =>
        store.ApplyTextEditsAsync(new Dictionary<int, string> { [9_999] = "nope" }, [], default));
}

[Fact]
public async Task ApplyInsertions_requires_a_finalized_session()
{
    var store = await NewUnfinalizedSessionAsync();
    await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Text.", 1_000, null)], [], 600_000, default));
}

[Fact]
public async Task ApplyInsertions_flips_meta_Edited()
{
    var store = await NewFinalizedSessionAsync(durationMs: 600_000);
    await store.ApplyInsertionsAsync(
        [new EditStore.InsertedLineWrite(null, "Text.", 1_000, null)], [], 600_000, default);
    var meta = await new MetadataStore(Path.Combine(_dir, "meta.json")).LoadAsync(default);
    Assert.True(meta!.Edited);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~EditStoreTests" -v q`
Expected: FAIL — `InsertedLineLabels` and `ApplyInsertionsAsync` do not exist.

- [ ] **Step 3: Create `InsertedLineLabels`**

Create `src/LocalScribe.Core/Projection/InsertedLineLabels.cs`:

```csharp
using System.Globalization;
namespace LocalScribe.Core.Projection;

/// <summary>The two fixed conventions a human-authored line depends on (design 2026-08-11 section
/// 2.3), in Core so the projection, the storage layer and the App editors cannot drift.</summary>
public static class InsertedLineLabels
{
    /// <summary>The display name for an inserted line with no participant, or one whose
    /// participant no longer exists in meta.json. A fixed label rather than null: NameResolver
    /// never returns null, so no non-marker DisplayRow has ever carried a null DisplayName and
    /// every renderer, the grouper and TranscriptCitation would be meeting a new state. British
    /// English, like every other user-facing string.</summary>
    public const string Unattributed = "Unattributed";

    private const string Prefix = "i";
    private const int Digits = 6;

    /// <summary>Mint the next insertion id: "i" + a zero-padded ordinal one past the HIGHEST
    /// existing one (not the count - a revert leaves a gap, and reusing a reverted id would make
    /// two different human acts share an identity in the manifest disclosure).
    ///
    /// Zero-padded on purpose: TranscriptProjection's final sort tiebreak compares these ids with
    /// StringComparer.Ordinal, so a fixed width makes ordinal string order EQUAL creation order for
    /// two insertions sharing a timestamp. Past 999999 insertions in one session the width grows
    /// and that equality lapses; the ids stay unique and the sort stays deterministic, which is all
    /// correctness needs. An unparseable key (a hand-edited edits.json) is skipped rather than
    /// throwing - it must never block a person from adding a line.
    ///
    /// NumberStyles.None + InvariantCulture are explicit rather than defaulted: a bare
    /// int.TryParse accepts leading signs and whitespace and follows the current culture, which is
    /// how "Source:id" parsing elsewhere in this repo became culture-dependent.</summary>
    public static string MintId(IEnumerable<string> existingIds)
    {
        int max = 0;
        foreach (string id in existingIds)
        {
            if (!id.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(id.AsSpan(Prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int n) && n > max)
                max = n;
        }
        return Prefix + (max + 1).ToString("D" + Digits, CultureInfo.InvariantCulture);
    }
}
```

- [ ] **Step 4: Add the write path to `EditStore`**

Add `using LocalScribe.Core.Projection;` to the top of `EditStore.cs`, then add these members
(place `ApplyInsertionsAsync` immediately after `RemoveSplitAsync`, so the three overlay writers sit
together):

```csharp
    /// <summary>One insertion to write. A null Id MINTS a new insertion; a non-null Id updates
    /// that existing one in place. Minting happens inside ApplyInsertionsAsync under the caller's
    /// per-session gate, so two edit-mode sections each adding a line cannot collide on an id.</summary>
    public sealed record InsertedLineWrite(string? Id, string Text, long StartMs,
        string? SpeakerParticipantId);

    /// <summary>Write and revert human-authored lines in ONE edits.json write and ONE meta.Edited
    /// flip (design 2026-08-11 section 2.3). Returns false - writing nothing, flipping nothing -
    /// when the whole batch was a no-op.
    ///
    /// This path DELIBERATELY does not call EnsureSegmentAsync/EnsureSegmentsAsync. Those exist to
    /// keep a correction or a split anchored to a machine line, and they are the layer that makes
    /// insertion impossible today; an inserted line has no machine line by definition. It must not
    /// loosen them for the other two overlays, which is why it is a separate method rather than a
    /// relaxed flag on an existing one.
    ///
    /// It also does NOT inherit ApplySplitAsync's fence. A split boundary must land in
    /// (line.StartMs, line.EndMs] because it partitions real machine speech; an inserted line needs
    /// the opposite freedom - any point in the session, precisely where the machine found nothing.
    ///
    /// <paramref name="sessionDurationMs"/> comes from SessionRecord.DurationMs. Zero means the
    /// record never got one (crash-recovered before finalize computed it) and the upper bound is
    /// skipped: refusing every insertion on such a session would block exactly the evidentiary
    /// repair this feature exists for. "No bound recorded" and "bound is zero" are different
    /// claims, the same distinction ManifestFile.FabricatedSilenceKnown draws.</summary>
    public async Task<bool> ApplyInsertionsAsync(IReadOnlyList<InsertedLineWrite> writes,
        IReadOnlyCollection<string> reverts, long sessionDurationMs, CancellationToken ct)
    {
        foreach (var w in writes)
        {
            if (string.IsNullOrWhiteSpace(w.Text))
                throw new ArgumentException(
                    "an inserted line is empty; transcript content is never blank (spec section 1.6).",
                    nameof(writes));
            if (w.StartMs < 0)
                throw new ArgumentException(
                    $"inserted line start {w.StartMs} is before the session start.", nameof(writes));
            if (sessionDurationMs > 0 && w.StartMs > sessionDurationMs)
                throw new ArgumentException(
                    $"inserted line start {w.StartMs} is past the session duration {sessionDurationMs}.",
                    nameof(writes));
            if (w.Id is { } id && reverts.Contains(id))
                throw new ArgumentException($"insertion {id} is both written and reverted.", nameof(reverts));
        }
        if (writes.Count == 0 && reverts.Count == 0) return false;

        await EnsureFinalizedAsync(ct);
        var edits = await LoadAsync(ct) ?? new Edits();
        var map = new Dictionary<string, InsertedLine>(edits.Insertions, StringComparer.Ordinal);
        bool changed = false;

        foreach (string id in reverts)
            if (map.Remove(id)) changed = true;

        var now = _time.GetUtcNow();
        foreach (var w in writes)
        {
            // Mint against the LIVE map so ids stay unique within this batch, not just against
            // what was on disk when it started.
            string id = w.Id ?? InsertedLineLabels.MintId(map.Keys);
            map[id] = new InsertedLine
            {
                Text = w.Text.Trim(),
                StartMs = w.StartMs,
                SpeakerParticipantId = w.SpeakerParticipantId,
                AddedAtUtc = now,
            };
            changed = true;
        }
        if (!changed) return false;

        await JsonFile.WriteAsync(EditsPath,
            edits with { SchemaVersion = WriteVersion(map), Insertions = map }, ct);
        await MarkEditedAsync(ct);
        return true;
    }

    /// <summary>The version's insertions alone, schema-guarded, without needing an EditStore
    /// instance. ManifestBuilder is the caller: it has a StoragePaths and no TimeProvider, and it
    /// must not construct a writer to perform a read.</summary>
    public static async Task<IReadOnlyDictionary<string, InsertedLine>> ReadInsertionsAsync(
        string editsPath, CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(editsPath, ct);
        if (obj is null) return new Dictionary<string, InsertedLine>(StringComparer.Ordinal);
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "edits.json");
        var edits = await JsonFile.ReadAsync<Edits>(editsPath, ct);
        return edits?.Insertions ?? new Dictionary<string, InsertedLine>(StringComparer.Ordinal);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~EditStoreTests" -v q`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2626 passed, 0 failed** (2613 + 13 new). Report the absolute numbers.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Projection/InsertedLineLabels.cs src/LocalScribe.Core/Storage/EditStore.cs tests/LocalScribe.Core.Tests/EditStoreTests.cs
git commit -m "feat(edits): write and revert human-authored lines through EditStore"
```

---

### Task 3: `TranscriptProjection` renders inserted lines

This is the choke point. One change lights up read view, edit view, `.docx`/`.md`/`.txt`,
`session.txt`, search, the semantic index, the MCP corpus and assistant citations.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/RowSegment.cs:10-17`
- Modify: `src/LocalScribe.Core/Projection/PreRow.cs:3-10` (doc only)
- Modify: `src/LocalScribe.Core/Projection/NameResolver.cs`
- Modify: `src/LocalScribe.Core/Projection/TranscriptProjection.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs`

**Interfaces:**
- Consumes: `Edits.Insertions`, `InsertedLine` (Task 1); `InsertedLineLabels.Unattributed` (Task 2).
- Produces:
  - `RowSegment` trailing member `string? InsertionId = null`
  - `NameResolver.ResolveInserted(string? participantId, SessionMeta meta) -> string`
  - `TranscriptProjection.InsertedRank` (`public const int = -1`)

**Critical:** `InsertionId` MUST be appended **after** `SpeakerClusterKey`. `RowSegment` is a
positional record and ~20 test files construct it positionally; inserting the member anywhere else
silently rebinds arguments.

- [ ] **Step 1: Write the failing tests**

Append to `tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs`. Read the top of that file
first and reuse its existing `Build`/`Seg`/`Meta` helpers rather than writing new ones; the sketch
below assumes a `Build(lines, speakers, edits, meta)` helper exists.

```csharp
private static Edits WithInsertions(params (string Id, long StartMs, string Text, string? Pid)[] ins)
    => new()
    {
        SchemaVersion = 2,
        Insertions = ins.ToDictionary(i => i.Id, i => new InsertedLine
        {
            Text = i.Text, StartMs = i.StartMs, SpeakerParticipantId = i.Pid,
            AddedAtUtc = DateTimeOffset.Parse("2026-08-11T10:00:00Z"),
        }),
    };

[Fact]
public void An_inserted_line_becomes_a_row_where_no_machine_segment_exists()
{
    var lines = new[] { Seg(seq: 0, startMs: 0, endMs: 1_000, "Machine text.") };
    var rows = Build(lines, speakers: null, WithInsertions(("i000001", 50_000, "Off-mic answer.", null)),
        MetaWith());

    var inserted = Assert.Single(rows, r => r.Text == "Off-mic answer.");
    Assert.False(inserted.IsMarker);
    Assert.Equal(50_000, inserted.StartMs);
    // Zero length: a human estimate is a point, not a measured span.
    Assert.Equal(50_000, inserted.EndMs);
    var seg = Assert.Single(inserted.Segments);
    Assert.Equal("i000001", seg.InsertionId);
}

[Fact]
public void An_inserted_line_resolves_its_participant_name()
{
    var meta = MetaWith(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
    var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
        WithInsertions(("i000001", 50_000, "Off-mic answer.", "p-1")), meta);

    Assert.Equal("Jane Okafor", Assert.Single(rows, r => r.Text == "Off-mic answer.").DisplayName);
}

[Fact]
public void An_inserted_line_with_no_participant_renders_Unattributed()
{
    var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
        WithInsertions(("i000001", 50_000, "Inaudible passage.", null)), MetaWith());

    Assert.Equal(InsertedLineLabels.Unattributed,
        Assert.Single(rows, r => r.Text == "Inaudible passage.").DisplayName);
}

[Fact]
public void An_inserted_line_whose_participant_no_longer_exists_renders_Unattributed()
{
    var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
        WithInsertions(("i000001", 50_000, "Orphaned.", "p-deleted")), MetaWith());

    Assert.Equal(InsertedLineLabels.Unattributed,
        Assert.Single(rows, r => r.Text == "Orphaned.").DisplayName);
}

[Fact]
public void An_inserted_line_sorts_BEFORE_a_machine_row_at_an_identical_start()
{
    // Both gestures are positional: "Add line here..." on a row seeds that row's start, and the
    // edit-mode "+" seeds a boundary. "Insert at N" means "before what is at N".
    var lines = new[] { Seg(seq: 0, startMs: 50_000, endMs: 51_000, "Machine text.") };
    var rows = Build(lines, null, WithInsertions(("i000001", 50_000, "Human text.", null)), MetaWith());

    var texts = rows.Where(r => !r.IsMarker).Select(r => r.Text).ToList();
    Assert.Equal(["Human text.", "Machine text."], texts);
}

[Fact]
public void Two_insertions_at_the_same_start_order_by_id()
{
    var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
        WithInsertions(("i000002", 50_000, "Second.", null), ("i000001", 50_000, "First.", null)),
        MetaWith());

    var texts = rows.Where(r => !r.IsMarker && r.StartMs == 50_000).SelectMany(
        r => r.Segments.Select(s => s.ProjectedText)).ToList();
    Assert.Equal(["First.", "Second."], texts);
}

[Fact]
public void An_inserted_line_is_never_counted_as_a_suppressed_segment()
{
    // suppressedSegmentCount feeds the "Omitted:" export line, an anti-concealment disclosure
    // about MACHINE omissions. A human addition is not one.
    var rows = new TranscriptProjection(NoVocab, NoDedup).Build(
        [Seg(0, 0, 1_000, "Machine.")], null,
        WithInsertions(("i000001", 50_000, "Human.", null)), MetaWith(), 5_000,
        out int suppressed);

    Assert.Equal(0, suppressed);
    Assert.Contains(rows, r => r.Text == "Human.");
}

[Fact]
public void An_inserted_line_is_never_marked_pinned_by_a_machine_pin_on_seq_0()
{
    // Machine seq starts at 0 (TranscriptStore.NextSeqAsync), so a placeholder Seq on an inserted
    // segment would collide with a REAL pinned line. Source is System, which speakers.json never
    // keys, and the projection skips the pin lookup for inserted rows outright.
    var speakers = new Speakers
    {
        Pinned = new Dictionary<string, IReadOnlyList<string>> { ["Local"] = ["0"] },
    };
    var rows = Build([Seg(0, 0, 1_000, "Machine.", TranscriptSource.Local)], speakers,
        WithInsertions(("i000001", 50_000, "Human.", null)), MetaWith());

    var seg = Assert.Single(Assert.Single(rows, r => r.Text == "Human.").Segments);
    Assert.False(seg.IsPinned);
    Assert.False(seg.IsCorrected);
    Assert.False(seg.IsSplitChild);
    Assert.Equal(TranscriptSource.System, seg.Source);
}

[Fact]
public void An_inserted_line_merges_into_an_adjacent_same_speaker_section()
{
    var meta = MetaWith(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
    var speakers = new Speakers
    {
        Assignments = new Dictionary<string, IReadOnlyDictionary<string, string>>
        { ["Remote"] = new Dictionary<string, string> { ["0"] = "Remote:1" } },
        Names = new Dictionary<string, string> { ["Remote:1"] = "Jane Okafor" },
    };
    var rows = Build([Seg(0, 10_000, 11_000, "and then I left.", TranscriptSource.Remote)],
        speakers, WithInsertions(("i000001", 10_000, "I locked the door", "p-1")), meta);

    var row = Assert.Single(rows, r => !r.IsMarker);
    Assert.Equal("I locked the door and then I left.", row.Text);
    Assert.Equal(2, row.Segments.Count);
    Assert.Equal("i000001", row.Segments[0].InsertionId);
    Assert.Null(row.Segments[1].InsertionId);
}

[Fact]
public void No_insertions_produces_byte_identical_rows_to_no_edits_at_all()
{
    var lines = new[] { Seg(0, 0, 1_000, "A."), Seg(1, 2_000, 3_000, "B.") };
    var withEmpty = Build(lines, null, new Edits { SchemaVersion = 1 }, MetaWith());
    var withNull = Build(lines, null, null, MetaWith());
    Assert.Equal(withNull, withEmpty);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~TranscriptProjectionTests" -v q`
Expected: FAIL — `RowSegment.InsertionId` does not exist, and inserted rows are not produced.

- [ ] **Step 3: Append `InsertionId` to `RowSegment`**

In `src/LocalScribe.Core/Projection/RowSegment.cs`, append the member **last** and extend the doc:

```csharp
public sealed record RowSegment(
    int Seq, TranscriptSource Source, long StartMs, long EndMs,
    string ProjectedText, string RawText, bool IsCorrected, bool IsPinned,
    bool IsSplitChild = false, int PartIndex = 0,
    // A split child's own speaker override, carried verbatim from the split part (edits.json) so
    // Edit mode can pre-select the part's dropdown instead of blanking it (Stage 6.1 re-edit fix).
    // Null on unsplit segments and on split parts that carry no explicit speaker.
    string? SpeakerParticipantId = null, string? SpeakerClusterKey = null,
    // Non-null marks a HUMAN-AUTHORED line (design 2026-08-11 section 2.3), keyed by its
    // edits.json insertions id. When it is set, Seq, PartIndex and Source carry NO MEANING and
    // must not be read: there is no machine line behind this row. Seq is 0 and machine seqs also
    // START at 0 (TranscriptStore.NextSeqAsync), so any consumer that groups or looks up by Seq
    // must partition on this member FIRST or it will collide with a real segment. Source is
    // TranscriptSource.System - "neither audio stream" - which is the one value speakers.json can
    // never key, so a forgotten guard degrades to a miss rather than to a wrong pin.
    // APPENDED LAST on purpose: this is a positional record built positionally in ~20 test files,
    // and inserting a member anywhere earlier silently rebinds every one of those arguments.
    string? InsertionId = null);
```

- [ ] **Step 4: Add `NameResolver.ResolveInserted`**

Append to `src/LocalScribe.Core/Projection/NameResolver.cs`:

```csharp
    /// <summary>The display name for a HUMAN-AUTHORED line (design 2026-08-11 section 2.3): a
    /// direct meta.json participant lookup, or the fixed Unattributed label.
    ///
    /// Deliberately NOT the cluster tiers above. speakers.json maps DIARISATION CLUSTERS to names
    /// and an inserted line has no cluster, so the participant id here is a different key space
    /// that must stay apart from clusterKey - which is also why an inserted line never enters
    /// speakers.json at all. A participant deleted from meta.json after the line was authored
    /// falls back to Unattributed rather than to a stale name: the same reasoning as a pin whose
    /// owner is no longer an offered candidate.</summary>
    public static string ResolveInserted(string? participantId, SessionMeta meta)
    {
        if (!string.IsNullOrEmpty(participantId))
        {
            var p = meta.Participants.FirstOrDefault(x => x.Id == participantId);
            if (p is not null && !string.IsNullOrEmpty(p.Name)) return p.Name;
        }
        return InsertedLineLabels.Unattributed;
    }
```

- [ ] **Step 5: Merge insertions into the projection**

In `src/LocalScribe.Core/Projection/TranscriptProjection.cs`:

Add the rank constant beside `Rank`:

```csharp
    /// <summary>The SourceRank a human-authored row takes (design 2026-08-11 section 2.3).
    /// NEGATIVE, so an inserted line sorts BEFORE a machine row at an identical StartMs. Both
    /// entry points are positional gestures - "Add line here..." seeds the clicked row's start, and
    /// the edit-mode "+" seeds a boundary - and "insert at N" means "before what is at N"; ranking
    /// after would make a line typed at the "+" jump below the following row, which reads as a bug.
    /// This decides an exact tie ONLY, and only against a human addition: no machine row's position
    /// relative to another machine row changes.
    /// Deliberately a plain int here rather than a new TranscriptSource value: LocalScribeJson
    /// registers JsonStringEnumConverter&lt;TranscriptSource&gt; with no unmapped fallback, so an
    /// older build meeting a new enum value on disk counts the line malformed and DROPS it
    /// (TranscriptStore) - the same silent disappearance this design rejects for TranscriptKind.</summary>
    public const int InsertedRank = -1;
```

Immediately after the marker loop (`:88-89`) and before the sort, add the insertion loop:

```csharp
        // Human-authored lines (design 2026-08-11 section 2.3). They join the row stream HERE,
        // after dedup deliberately: suppressedSegmentCount feeds the "Omitted:" export line, which
        // is an anti-concealment disclosure about MACHINE omissions, and a human addition is not
        // one. They are also never looked up in speakers.json Pinned - an inserted line has no
        // cluster and no machine seq, and machine seqs START at 0, so a lookup keyed on the
        // placeholder Seq would collide with a real pinned segment.
        if (edits is not null)
            foreach (var (id, ins) in edits.Insertions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                pre.Add(new PreRow(ins.StartMs, ins.StartMs, InsertedRank, Seq: 0,
                    NameResolver.ResolveInserted(ins.SpeakerParticipantId, meta), ins.Text,
                    IsMarker: false,
                    Segment: new RowSegment(Seq: 0, TranscriptSource.System, ins.StartMs, ins.StartMs,
                        ProjectedText: ins.Text, RawText: ins.Text, IsCorrected: false, IsPinned: false,
                        // Carried through verbatim so Edit mode can pre-select the dropdown by
                        // TARGET rather than by display string. The member already exists for
                        // exactly this job on split children; reusing it means the id makes a full
                        // round trip and a save never writes a display name back into storage.
                        SpeakerParticipantId: ins.SpeakerParticipantId,
                        InsertionId: id)));
```

Note: the `RowSegment` above is constructed with **named** arguments past `IsPinned` so the two
optional split members are skipped safely. `EndMs == StartMs` is deliberate - a human estimate is a
point, not a measured span - and `SectionGrouper` handles it correctly (`Math.Max` on the running
end, and a zero-or-negative gap merges).

Add one more test to Step 1's set, pinning the round trip Task 8 depends on:

```csharp
[Fact]
public void An_inserted_row_segment_carries_its_participant_id_for_the_editor_to_preselect()
{
    var meta = MetaWith(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
    var rows = Build([Seg(0, 0, 1_000, "Machine.")], null,
        WithInsertions(("i000001", 50_000, "Off-mic answer.", "p-1")), meta);

    var seg = Assert.Single(Assert.Single(rows, r => r.Text == "Off-mic answer.").Segments);
    Assert.Equal("p-1", seg.SpeakerParticipantId);
    Assert.Null(seg.SpeakerClusterKey);   // never a clusterKey - a different key space entirely
}
```

Extend the comparator with the fourth key:

```csharp
        // (6): order (startMs, source rank, seq, insertion id) then group by speaker + silence gap.
        // The fourth key reads off Segment rather than a member of its own so the id has ONE home:
        // two insertions can share a StartMs and share the InsertedRank, and Seq is a placeholder
        // 0 on both. Ids are fixed-width zero-padded (InsertedLineLabels.MintId), so ordinal order
        // here IS creation order. Machine rows carry a null id and never reach this key - their
        // ranks differ from InsertedRank, so Seq has already decided.
        pre.Sort((a, b) =>
        {
            int c = a.StartMs.CompareTo(b.StartMs);
            if (c != 0) return c;
            c = a.SourceRank.CompareTo(b.SourceRank);
            if (c != 0) return c;
            c = a.Seq.CompareTo(b.Seq);
            return c != 0 ? c
                : StringComparer.Ordinal.Compare(a.Segment?.InsertionId, b.Segment?.InsertionId);
        });
```

- [ ] **Step 6: Update `PreRow`'s doc comment**

In `src/LocalScribe.Core/Projection/PreRow.cs`, append to the summary:

```csharp
/// A human-authored row (design 2026-08-11 section 2.3) takes TranscriptProjection.InsertedRank,
/// Seq 0, and EndMs == StartMs; its identity lives on Segment.InsertionId, which is also the
/// sort's fourth and final tiebreak. Seq is a placeholder on such a row and is only ever compared
/// against another inserted row's, because no machine row shares InsertedRank.
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~TranscriptProjectionTests" -v q`
Expected: PASS.

- [ ] **Step 8: Prove the test is real**

Temporarily change `InsertedRank` to `3` and re-run
`An_inserted_line_sorts_BEFORE_a_machine_row_at_an_identical_start`. Expected: FAIL. Restore `-1`
and confirm PASS. Do not commit the temporary change.

- [ ] **Step 9: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2637 passed, 0 failed** (2626 + 11 new). Report the absolute numbers. Confirm the
`CS8602` count is still exactly 7.

- [ ] **Step 10: Commit**

```bash
git add src/LocalScribe.Core/Projection/ tests/LocalScribe.Core.Tests/TranscriptProjectionTests.cs
git commit -m "feat(projection): render human-authored lines from the insertions overlay"
```

---

### Task 4: An inserted line flips the export disclaimer

Part 2's whole export obligation. Part 3 removed the printed counts line; `HumanLayerCounts`
survives to feed `MetadataFormat.DisclaimerFor` and `MetadataFormat.OmittedLine`.

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ExportProvenance.cs:15-35`
- Modify: `src/LocalScribe.Core/Projection/MetadataFormat.cs:42-46`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:1131-1155`
- Test: `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs` (or the file that currently covers
  `ProvenanceFor` — locate it with `grep -rn "ProvenanceFor" tests/`)

**Interfaces:**
- Consumes: `Edits.Insertions` (Task 1).
- Produces: `HumanLayerCounts.Insertions` (int).

**Read first:** the doc comment on `HumanLayerCounts.SpeakerNames`
(`src/LocalScribe.Core/Projection/ExportProvenance.cs:22-30`). It records why that count is not
`Names.Count`: `SpeakerDetectionStep` writes `DefaultSpeakerLabels.For(...)` for every cluster an
auto-detected import finds, with no user interaction, so counting raw entries let a wholly automatic
import select the "reviewed and corrected by a person" disclaimer for a document nobody had opened,
served on opposing counsel. **Whatever you add here must be provably written only by a human.**

For `Insertions` that proof is: the only production writer of `Edits.Insertions` is
`EditStore.ApplyInsertionsAsync`, reachable only from `MaintenanceService.SaveTranscriptEditsAsync`
(Task 7), reachable only from a UI gesture. Step 1 pins it with a test over the automatic import
path rather than asserting it in prose.

- [ ] **Step 1: Write the failing tests**

In `tests/LocalScribe.Core.Tests/MetadataFormatTests.cs`:

```csharp
[Fact]
public void DisclaimerFor_selects_the_edited_variant_when_a_person_inserted_a_line()
{
    Assert.Equal(ExportNotices.DisclaimerEdited,
        MetadataFormat.DisclaimerFor(new HumanLayerCounts { Insertions = 1 }));
}

[Fact]
public void DisclaimerFor_still_selects_untouched_when_only_dedup_ran()
{
    // Part 3's ruling, unchanged: PhantomBleedDedup is a machine act.
    Assert.Equal(ExportNotices.Disclaimer,
        MetadataFormat.DisclaimerFor(new HumanLayerCounts { SuppressedDuplicates = 4 }));
}

[Fact]
public void DisclaimerFor_still_selects_untouched_for_machine_default_speaker_names()
{
    // Part 3 whole-branch review finding 1, guarded again now that a fifth count exists.
    Assert.Equal(ExportNotices.Disclaimer,
        MetadataFormat.DisclaimerFor(new HumanLayerCounts
        { SpeakerNames = 0, SuppressedDuplicates = 0 }));
}
```

In the App test file that covers `ProvenanceFor`:

```csharp
[Fact]
public void ProvenanceFor_counts_insertions_from_the_edits_overlay()
{
    var loaded = LoadedWith(edits: new Edits
    {
        SchemaVersion = 2,
        Insertions = new Dictionary<string, InsertedLine>
        {
            ["i000001"] = new InsertedLine { Text = "a", StartMs = 1 },
            ["i000002"] = new InsertedLine { Text = "b", StartMs = 2 },
        },
    });

    Assert.Equal(2, MaintenanceService.ProvenanceFor(loaded).HumanLayer!.Insertions);
}

[Fact]
public async Task An_automatic_import_with_detection_never_claims_a_person_reviewed_it()
{
    // The Part 3 defect class, re-run now that a fifth human count exists: nothing on the
    // machine's own path may write Edits.Insertions. Import + auto speaker detection, no user
    // gesture at all, must still select the UNTOUCHED disclaimer.
    string sessionId = await ImportWithAutoDetectionAsync();
    var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings, _time, sessionId);

    var counts = MaintenanceService.ProvenanceFor(loaded).HumanLayer!;
    Assert.Equal(0, counts.Insertions);
    Assert.Equal(ExportNotices.Disclaimer, MetadataFormat.DisclaimerFor(counts));
}
```

For `ImportWithAutoDetectionAsync`, reuse whatever harness the existing import + detection tests
already use — search `tests/LocalScribe.App.Tests/` for `SpeakerDetectionStep` and copy that
fixture's setup. If no such harness exists, assert the narrower fact instead: run the importer,
load the projection, and assert `loaded.Edits?.Insertions` is null or empty and the disclaimer is
`ExportNotices.Disclaimer`. Say which of the two you used in your report.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture&(FullyQualifiedName~MetadataFormatTests|FullyQualifiedName~ProvenanceFor)" -v q`
Expected: FAIL — `HumanLayerCounts.Insertions` does not exist.

- [ ] **Step 3: Add the count**

In `src/LocalScribe.Core/Projection/ExportProvenance.cs`, add after `Splits` and update the record's
summary from "Five separate counts" to "Six separate counts":

```csharp
    /// <summary>Lines a PERSON authored where the machine produced nothing (edits.json insertions,
    /// design 2026-08-11 section 2.3). Its OWN count rather than folded into Corrections, per this
    /// record's rule that each count maps to exactly one on-disk structure: a correction alters
    /// machine text, an insertion creates text the machine never produced, and merging them would
    /// make the disclaimer's evidence unreadable.
    ///
    /// Unlike SpeakerNames, this member needs no machine-vs-human discrimination: the only
    /// production writer of Edits.Insertions is EditStore.ApplyInsertionsAsync, which is reachable
    /// only from MaintenanceService.SaveTranscriptEditsAsync and therefore only from a UI gesture.
    /// Nothing on the import, diarisation or detection paths writes it. That claim is pinned by a
    /// test over the automatic import path, not asserted here - it is exactly the claim
    /// SpeakerNames turned out not to satisfy.</summary>
    public int Insertions { get; init; }
```

- [ ] **Step 4: Extend the disclaimer predicate**

In `src/LocalScribe.Core/Projection/MetadataFormat.cs`:

```csharp
    public static string DisclaimerFor(HumanLayerCounts? counts)
        => counts is { } c
           && (c.Corrections > 0 || c.Splits > 0 || c.SpeakerPins > 0 || c.SpeakerNames > 0
               || c.Insertions > 0)
            ? ExportNotices.DisclaimerEdited
            : ExportNotices.Disclaimer;
```

Extend the method's doc with:

```
    /// Insertions joined the predicate in Part 2 (design 2026-08-11 section 2.3): a document
    /// containing a line a person wrote must never print "no person has touched this". Part 3 left
    /// this predicate alone as correct FOR THE COUNTS THAT THEN EXISTED - adding a fifth human act
    /// without adding it here would silently make the disclaimer wrong again.
```

- [ ] **Step 5: Compose it in `ProvenanceFor`**

In `src/LocalScribe.App/Services/MaintenanceService.cs:1136-1154`, add inside the
`new HumanLayerCounts { ... }` initializer, after `Splits`:

```csharp
                // Human-authored lines (design 2026-08-11 section 2.3). A plain count of the
                // overlay map: unlike SpeakerNames, nothing machine-side ever writes it.
                Insertions = loaded.Edits?.Insertions.Count ?? 0,
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture&(FullyQualifiedName~MetadataFormatTests|FullyQualifiedName~ProvenanceFor)" -v q`
Expected: PASS.

- [ ] **Step 7: Prove the disclaimer test is real**

Temporarily remove `|| c.Insertions > 0` and re-run
`DisclaimerFor_selects_the_edited_variant_when_a_person_inserted_a_line`. Expected: FAIL. Restore
and confirm PASS. Do not commit the temporary change.

- [ ] **Step 8: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2642 passed, 0 failed** (2637 + 5 new). Report the absolute numbers.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.Core/Projection/ExportProvenance.cs src/LocalScribe.Core/Projection/MetadataFormat.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/
git commit -m "feat(export): an inserted line flips the disclaimer to the reviewed variant"
```

---

### Task 5: Downstream consumers — search, MCP, citations, and the raw-JSONL guard

**Files:**
- Modify: `src/LocalScribe.Core/Search/SearchModels.cs:10-11`
- Modify: `src/LocalScribe.Core/Search/SearchIndexBuilder.cs:25`
- Modify: `src/LocalScribe.Core/Mcp/McpDtos.cs:28-29`
- Modify: `src/LocalScribe.Core/Mcp/McpCorpus.cs:263-273`, `:299-330`, `:363-365`
- Test: `tests/LocalScribe.Core.Tests/SearchIndexBuilderTests.cs`
- Test: `tests/LocalScribe.Core.Tests/McpCorpusTests.cs` (locate the actual filename with
  `grep -rln "ReadTranscriptAsync" tests/`)
- Test: `tests/LocalScribe.Core.Tests/TranscriptCitationTests.cs`
- Test: new `tests/LocalScribe.Core.Tests/InsertedLineIsolationTests.cs`

**Interfaces:**
- Consumes: `RowSegment.InsertionId` (Task 3).
- Produces: `SearchLine` trailing `string? InsertionId = null`; `McpTranscriptRowDto` trailing
  `string? InsertionId = null`.

Both are positional records; append the member **last**. `SearchLine` is referenced by 3 test files,
`McpTranscriptRowDto` by 0 — treat the MCP layer as effectively uncovered and write the tests
below rather than assuming coverage exists.

No index schema bump: `SearchIndexCache.SchemaVersion` gates a regenerable **cache**, and staleness
is decided by `SearchFreshnessStamps`' edits.json ticks, which change on every insertion write. An
older build reading a newer cache skips the unknown member (`LocalScribeJson` sets no
`UnmappedMemberHandling`, so the default `Skip` applies) and rebuilds the entry anyway.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/SearchIndexBuilderTests.cs`:

```csharp
[Fact]
public async Task An_inserted_line_is_indexed_and_carries_its_insertion_id()
{
    string sessionId = await SessionWithInsertionAsync("Off-mic answer.", startMs: 50_000);
    var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, _settings, _time, sessionId);

    var line = Assert.Single(entry.Lines, l => l.Text == "Off-mic answer.");
    Assert.Equal("i000001", line.InsertionId);
    Assert.Null(line.OriginalText);   // there is no machine original to differ from
}
```

`tests/LocalScribe.Core.Tests/TranscriptCitationTests.cs`:

```csharp
[Fact]
public void An_inserted_row_cites_with_no_seq_and_round_trips_through_the_validator()
{
    // The citation format is timestamp-based and carries no seq at all, so a human-authored line
    // needs NO new surface form. Nothing currently pins that; this does.
    var row = new DisplayRow
    {
        StartMs = 3_725_000, DisplayName = "Jane Okafor", Text = "Off-mic answer.",
        Segments = [new RowSegment(0, TranscriptSource.System, 3_725_000, 3_725_000,
            "Off-mic answer.", "Off-mic answer.", false, false, InsertionId: "i000001")],
    };

    string citation = TranscriptCitation.Format(row, "Smith v Jones",
        DateTimeOffset.Parse("2026-08-11T09:00:00+01:00"), "v1");

    Assert.DoesNotContain("i000001", citation, StringComparison.Ordinal);
    Assert.Contains(AssistantCitationFormat.Format(3_725_000), citation, StringComparison.Ordinal);
    // The stamp the assistant emits for this row is one the validator accepts.
    Assert.Matches(AssistantCitationFormat.StampPattern, AssistantCitationFormat.Format(3_725_000));
}

[Fact]
public void An_unattributed_inserted_row_drops_the_speaker_clause_rather_than_citing_an_empty_name()
{
    var row = new DisplayRow
    {
        StartMs = 60_000, DisplayName = InsertedLineLabels.Unattributed, Text = "Inaudible.",
        Segments = [new RowSegment(0, TranscriptSource.System, 60_000, 60_000,
            "Inaudible.", "Inaudible.", false, false, InsertionId: "i000001")],
    };
    Assert.Contains(InsertedLineLabels.Unattributed,
        TranscriptCitation.Format(row, "T", DateTimeOffset.UnixEpoch, "v1"), StringComparison.Ordinal);
}
```

If `AssistantCitationFormat` exposes no public `StampPattern`, use the literal
`@"\[(\d{1,3}(?::\d{1,2}){1,2})\]"` and add a comment naming `CitationValidator` as its owner.

MCP tests:

```csharp
[Fact]
public async Task ReadTranscript_returns_an_inserted_row_with_a_null_seq_and_its_insertion_id()
{
    string sessionId = await SessionWithInsertionAsync("Off-mic answer.", startMs: 50_000);
    var res = await _corpus.ReadTranscriptAsync(sessionId, null, null, null, 0, null, default);

    var row = Assert.Single(res.Rows, r => r.Text == "Off-mic answer.");
    Assert.Equal("speech", row.Kind);
    Assert.Null(row.Seq);
    Assert.Null(row.PartIndex);
    Assert.Equal("i000001", row.InsertionId);
}

[Fact]
public async Task A_seq_bounded_read_is_never_ANCHORED_by_an_inserted_row()
{
    // An inserted unit has no seq. Letting a placeholder 0 satisfy "u.Seq <= toSeq" would let a
    // line at the END of a long transcript extend the span and return far more than was asked for.
    string sessionId = await SessionWithSegmentsAndTrailingInsertionAsync();
    var res = await _corpus.ReadTranscriptAsync(sessionId, fromSeq: null, toSeq: 1,
        aroundSeq: null, context: 0, cursor: null, default);

    Assert.All(res.Rows, r => Assert.True(r.Seq is null or <= 1));
    Assert.DoesNotContain(res.Rows, r => r.Text == "Trailing inserted line.");
}

[Fact]
public async Task An_inserted_row_INSIDE_a_seq_span_is_still_returned()
{
    // The opposite failure: silently omitting human-authored content from a bounded read is
    // concealment. The span is index-based, so anything between the anchors travels with them.
    string sessionId = await SessionWithInsertionBetweenSeqs0And2Async();
    var res = await _corpus.ReadTranscriptAsync(sessionId, fromSeq: 0, toSeq: 2,
        aroundSeq: null, context: 0, cursor: null, default);

    Assert.Contains(res.Rows, r => r.InsertionId == "i000001");
}

[Fact]
public async Task AroundSeq_never_centres_on_an_inserted_row()
{
    string sessionId = await SessionWithSegmentsAndTrailingInsertionAsync();
    var res = await _corpus.ReadTranscriptAsync(sessionId, null, null, aroundSeq: 0,
        context: 0, cursor: null, default);

    Assert.Equal(0, Assert.Single(res.Rows).Seq);
}
```

New file `tests/LocalScribe.Core.Tests/InsertedLineIsolationTests.cs`:

```csharp
namespace LocalScribe.Core.Tests;

/// <summary>The three consumers that read transcript.jsonl LINES rather than projected DisplayRows
/// must never see a human-authored line: it carries no voice evidence to cluster or enrol from.
/// This is free while insertions stay out of transcript.jsonl - and this file is what fails if
/// someone later "helpfully" merges them in.</summary>
public class InsertedLineIsolationTests
{
    [Fact]
    public async Task An_insertion_never_reaches_transcript_jsonl()
    {
        string sessionId = await SessionWithInsertionAsync("Off-mic answer.", startMs: 50_000);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(default);

        Assert.DoesNotContain(lines, l => l.Text == "Off-mic answer.");
        Assert.All(lines, l => Assert.True(
            l.Kind is TranscriptKind.Segment or TranscriptKind.Marker));
    }

    [Fact]
    public async Task The_jsonl_line_count_is_unchanged_by_an_insertion()
    {
        // SegmentCount + MarkerCount arithmetic lives in SIX places; an overlay keeps all six true.
        string sessionId = await SessionWithSegmentsAsync(segments: 3, markers: 1);
        var before = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(default);
        await InsertLineAsync(sessionId, "Off-mic answer.", 50_000);
        var after = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(default);

        Assert.Equal(before.Count, after.Count);
        var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(default);
        Assert.Equal(after.Count, session!.SegmentCount + session.MarkerCount);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture&(FullyQualifiedName~SearchIndexBuilder|FullyQualifiedName~TranscriptCitation|FullyQualifiedName~McpCorpus|FullyQualifiedName~InsertedLineIsolation)" -v q`
Expected: FAIL — the two `InsertionId` members do not exist, and the MCP seq-range tests fail on
the anchor bug.

- [ ] **Step 3: Extend `SearchLine` and its builder**

`src/LocalScribe.Core/Search/SearchModels.cs`:

```csharp
public sealed record SearchLine(int Seq, int PartIndex, long StartMs, string Text,
    string? OriginalText, string Speaker, string? InsertionId = null);
```

Extend the record's doc with:

```
/// InsertionId is non-null for a HUMAN-AUTHORED line (design 2026-08-11 section 2.3); Seq and
/// PartIndex are then placeholders and must not be used to address it. Appended last - this is a
/// positional record. No SearchIndexCache schema bump: the cache is regenerable and staleness is
/// decided by SearchFreshnessStamps' edits.json ticks, which move on every insertion write.
```

`src/LocalScribe.Core/Search/SearchIndexBuilder.cs:25`:

```csharp
                lines.Add(new SearchLine(seg.Seq, seg.PartIndex, seg.StartMs,
                    Text: seg.ProjectedText,
                    OriginalText: seg.IsCorrected
                        && !string.Equals(seg.RawText, seg.ProjectedText, StringComparison.Ordinal)
                        ? seg.RawText : null,
                    Speaker: row.DisplayName ?? "",
                    InsertionId: seg.InsertionId));
```

- [ ] **Step 4: Extend the MCP DTO and corpus**

`src/LocalScribe.Core/Mcp/McpDtos.cs`:

```csharp
public sealed record McpTranscriptRowDto(string Kind, int? Seq, int? PartIndex, long StartMs, long EndMs,
    string? Speaker, string Text, string? InsertionId = null);
```

`src/LocalScribe.Core/Mcp/McpCorpus.cs` — extend `ReadUnit` and `FlattenToUnits`:

```csharp
    private readonly record struct ReadUnit(bool IsMarker, long StartMs, long EndMs,
        string? Speaker, string Text, int Seq, int? PartIndex, string? InsertionId = null);
```

```csharp
            if (row.IsMarker)
            { units.Add(new ReadUnit(true, row.StartMs, row.EndMs, null, row.Text, -1, null)); continue; }
            foreach (var seg in row.Segments)
                units.Add(new ReadUnit(false, seg.StartMs, seg.EndMs, row.DisplayName, seg.ProjectedText,
                    seg.Seq, seg.PartIndex, seg.InsertionId));
```

Exclude inserted units from the seq predicate (`:302`):

```csharp
            // A human-authored unit has NO seq (design 2026-08-11 section 2.3), so it must never
            // ANCHOR a seq-bounded span: its placeholder Seq 0 would satisfy "<= toSeq" and let a
            // line at the far end of the transcript stretch the span far past what was asked for.
            // It is still RETURNED when it falls inside the span, because the span is index-based -
            // silently dropping human-authored content from a bounded read would be concealment.
            !u.IsMarker && u.InsertionId is null
                && (fromSeq is null || u.Seq >= fromSeq) && (toSeq is null || u.Seq <= toSeq);
```

Apply the same guard to the three `aroundSeq` centring comparisons (`:315`, `:318`, `:329`) by
adding `&& units[i].InsertionId is null` / `&& u.InsertionId is null` to each.

Pass the id through at `:365`:

```csharp
                : new McpTranscriptRowDto("speech",
                    u.InsertionId is null ? u.Seq : null,
                    u.InsertionId is null ? u.PartIndex : null,
                    u.StartMs, u.EndMs, u.Speaker, u.Text, u.InsertionId));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture&(FullyQualifiedName~SearchIndexBuilder|FullyQualifiedName~TranscriptCitation|FullyQualifiedName~McpCorpus|FullyQualifiedName~InsertedLineIsolation)" -v q`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2651 passed, 0 failed** (2642 + 9 new). Report the absolute numbers.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Search/ src/LocalScribe.Core/Mcp/ tests/LocalScribe.Core.Tests/
git commit -m "feat(consumers): carry InsertionId through search and the MCP corpus"
```

---

### Task 5b: the assistant must not ground a citation in the wrong line (ADDED — Task 5 review)

**Why this task exists.** The plan's blast-radius table counted `SearchLine`'s test files but never
traced its *consumers*. `SearchQueryEngine` builds `SearchHit` from `SearchLine.Seq`/`PartIndex`
(`SearchQueryEngine.cs:84`, `:88`, `:115`), and `ExcerptContextBuilder` resolves a hit back to a row
with:

```csharp
if (hit.Seq < 0) continue;
int si = spoken.FindIndex(r => r.Segments.Any(
    g => g.Seq == hit.Seq && g.PartIndex == hit.PartIndex));
if (si < 0) si = NearestByStart(spoken, hit.StartMs);
```

An inserted line's hit carries the placeholder `Seq = 0, PartIndex = 0`. Machine seq also starts at
0, so `FindIndex` returns the **first machine segment 0** — and it *succeeds*, so the
`NearestByStart` fallback never fires. A search term matching text a person typed at 45:00 grounds
the assistant's answer, and its citation, in a machine segment at 00:00. In an evidentiary product
that is a wrong attribution of words to a moment in the recording.

**The collision runs both ways** and the fix must close both: a *machine* hit on seq 0 can equally
match the *inserted* segment, because that segment also reports `Seq = 0, PartIndex = 0`.

`SearchHit`'s other consumer is display-only (`SessionsPageViewModel.cs:763` formats a snippet), so
nothing else addresses by seq. Scope is these three files.

**Files:**
- Modify: `src/LocalScribe.Core/Search/SearchModels.cs` (`SearchHit`, `:68`)
- Modify: `src/LocalScribe.Core/Search/SearchQueryEngine.cs:84`, `:88`, `:115`
- Modify: `src/LocalScribe.Core/Assistant/ExcerptContextBuilder.cs:52-55`
- Test: the existing `SearchQueryEngine` test file and the existing `ExcerptContextBuilder` test file
  (locate both with `grep -rln "SearchQueryEngine\|ExcerptContextBuilder" tests/`)

**Interfaces:**
- Consumes: `SearchLine.InsertionId` (Task 5); `RowSegment.InsertionId` (Task 3).
- Produces: `SearchHit` trailing `string? InsertionId = null`.

`SearchHit` is a positional record — append the member **last**.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void A_hit_on_an_inserted_line_carries_its_insertion_id()
{
    var entry = EntryWith(
        new SearchLine(0, 0, 0, "Machine words.", null, "Me"),
        new SearchLine(0, 0, 45_000, "Authored words.", null, "Jane", InsertionId: "i000001"));

    var hit = Assert.Single(Hits(entry, "authored"));
    Assert.Equal("i000001", hit.InsertionId);
}

[Fact]
public void A_hit_on_a_machine_line_carries_no_insertion_id()
{
    var entry = EntryWith(new SearchLine(0, 0, 0, "Machine words.", null, "Me"));
    Assert.Null(Assert.Single(Hits(entry, "machine")).InsertionId);
}
```

```csharp
[Fact]
public void An_inserted_line_hit_excerpts_the_inserted_row_not_machine_segment_zero()
{
    // Both rows report Seq 0 / PartIndex 0 - the inserted one because that is its placeholder,
    // the machine one because machine seq genuinely starts at 0. Matching on (Seq, PartIndex)
    // alone returns the machine row and grounds the assistant's citation at the wrong moment.
    var rows = new[]
    {
        RowWith(0, 0, "Machine words at the start.", startMs: 0),
        InsertedRowWith("i000001", "Authored words much later.", startMs: 45_000),
    };
    var hit = new SearchHit(0, 0, 45_000, "Jane", "Authored words much later.", "authored",
        false, false, InsertionId: "i000001");

    var ctx = BuildExcerpt(rows, hit, term: "authored");

    Assert.Contains("Authored words much later.", ctx.Text, StringComparison.Ordinal);
    Assert.DoesNotContain("Machine words at the start.", ctx.Text, StringComparison.Ordinal);
}

[Fact]
public void A_machine_hit_on_seq_zero_excerpts_the_machine_row_not_an_inserted_one()
{
    // The collision runs both ways: without the InsertionId-is-null half of the guard, a machine
    // hit can match the inserted segment, which also reports Seq 0 / PartIndex 0.
    var rows = new[]
    {
        InsertedRowWith("i000001", "Authored words.", startMs: 0),
        RowWith(0, 0, "Machine words.", startMs: 1_000),
    };
    var hit = new SearchHit(0, 0, 1_000, "Me", "Machine words.", "machine", false, false);

    var ctx = BuildExcerpt(rows, hit, term: "machine");

    Assert.Contains("Machine words.", ctx.Text, StringComparison.Ordinal);
}
```

Adapt the helper names to whatever the two test files already provide; add the smallest helper you
need rather than a parallel fixture stack.

- [ ] **Step 2: Run the tests to verify they fail**

Expected: FAIL — `SearchHit` has no `InsertionId`, and the excerpt test returns the machine row.

- [ ] **Step 3: Carry the id onto `SearchHit`**

```csharp
public sealed record SearchHit(int Seq, int PartIndex, long StartMs, string Speaker,
    string Snippet, string MatchedTerm, bool MatchesOriginalOnly, bool IsSpeakerNameMatch,
    // Non-null marks a hit on a HUMAN-AUTHORED line (design 2026-08-11 section 2.3); Seq and
    // PartIndex are then placeholders and must not be used to address it. Appended last - this is
    // a positional record.
    string? InsertionId = null);
```

Pass `line.InsertionId` at `SearchQueryEngine.cs:84`, `:88` and `:115`. Leave `:118` (the
named-participant-with-no-resolved-line case, `Seq -1`) at the default null.

- [ ] **Step 4: Fix the match in `ExcerptContextBuilder`**

```csharp
                    if (hit.Seq < 0) continue;   // speaker-name hit: no spoken line to excerpt
                    // A human-authored line has no machine seq: its Seq/PartIndex are placeholder
                    // zeros and machine seq STARTS at 0, so matching on the pair alone resolves an
                    // inserted hit to the first machine segment 0 - and it SUCCEEDS, so the
                    // NearestByStart fallback below never fires and the assistant grounds its
                    // citation at the wrong moment in the recording. The null half of the machine
                    // branch closes the same collision in the other direction.
                    int si = spoken.FindIndex(r => r.Segments.Any(
                        g => hit.InsertionId is { } id
                            ? g.InsertionId == id
                            : g.InsertionId is null && g.Seq == hit.Seq && g.PartIndex == hit.PartIndex));
```

- [ ] **Step 5: Run the tests to verify they pass**

- [ ] **Step 6: Prove both halves of the guard are real**

Revert the match to `g.Seq == hit.Seq && g.PartIndex == hit.PartIndex` and confirm
`An_inserted_line_hit_excerpts_the_inserted_row_not_machine_segment_zero` FAILS. Restore.
Then drop only the `g.InsertionId is null &&` clause and confirm
`A_machine_hit_on_seq_zero_excerpts_the_machine_row_not_an_inserted_one` FAILS. Restore. Capture
both failures. Do not commit the temporary changes.

- [ ] **Step 7: Run the full gate**

Expected: **2660 passed, 0 failed** (2656 + 4 new).

- [ ] **Step 8: Commit**

```bash
git add src/LocalScribe.Core/Search/ src/LocalScribe.Core/Assistant/ExcerptContextBuilder.cs tests/
git commit -m "fix(assistant): never ground an excerpt in machine seq 0 for a human-authored hit"
```

---

### Task 6: `manifest.json` discloses inserted lines

**Files:**
- Modify: `src/LocalScribe.Core/Model/SessionManifest.cs`
- Modify: `src/LocalScribe.Core/Storage/ManifestBuilder.cs`
- Test: `tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs`

**Interfaces:**
- Consumes: `EditStore.ReadInsertionsAsync` (Task 2).
- Produces: `InsertedLineRef` record `(string Id, long StartMs, DateTimeOffset AddedAtUtc)`;
  `SessionManifest.InsertedLines`.

**Two rulings this task must honour:**

1. **No schema bump.** `ManifestStore.ReadAsync:19` calls
   `SchemaGuard.RejectIfNewer(version, Version, "manifest.json")`. Bumping would make an older build
   **reject** the manifest — and a session whose seal cannot be read is exactly what
   `IntegrityReport` treats as unsealed. That is a false tamper verdict on an untampered session, the
   thing this file's doc forbids. `LocalScribeJson` sets no `UnmappedMemberHandling`, so the default
   `Skip` applies and an older build simply ignores the new member.
   Nothing needs changing to hold version 1: `ManifestStore.WriteAsync:24` stamps
   `SchemaVersion = Version` on every write, and `ManifestStore.Version` stays `1`. Leave both
   alone — the test in Step 1 asserts they still agree.
2. **No tri-state.** `ManifestFile.FabricatedSilenceKnown` exists because a leg's fabricated ranges
   genuinely can be unknown — imported audio, crash recovery, or a build predating the feature. There
   is no equivalent unknown here: the manifest is rebuilt from `edits.json` on every reseal, and an
   older build's manifest lacking the member reads as an empty list, which is **true** — that build
   cannot create insertions at all. Adding a `Known` flag would invent a state that cannot occur.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task The_manifest_discloses_each_inserted_line()
{
    string sessionId = await SessionWithInsertionAsync("Off-mic answer.", startMs: 50_000);
    var manifest = await ManifestBuilder.BuildAsync(_paths, sessionId, TranscriptVersions.Root,
        _now, fabricated: null, sealAudio: false, default);

    var disclosed = Assert.Single(manifest.InsertedLines);
    Assert.Equal("i000001", disclosed.Id);
    Assert.Equal(50_000, disclosed.StartMs);
}

[Fact]
public async Task A_session_with_no_insertions_discloses_an_empty_list()
{
    string sessionId = await SessionWithSegmentsAsync(segments: 2, markers: 0);
    var manifest = await ManifestBuilder.BuildAsync(_paths, sessionId, TranscriptVersions.Root,
        _now, null, false, default);

    Assert.Empty(manifest.InsertedLines);
}

[Fact]
public async Task The_manifest_schema_version_is_NOT_bumped_by_the_disclosure()
{
    // A bump would make an OLDER build reject the manifest outright, which IntegrityReport reads
    // as unsealed - a false tamper verdict on a session nobody touched.
    string sessionId = await SessionWithInsertionAsync("Off-mic answer.", 50_000);
    var manifest = await ManifestBuilder.BuildAsync(_paths, sessionId, TranscriptVersions.Root,
        _now, null, false, default);

    Assert.Equal(1, manifest.SchemaVersion);
    Assert.Equal(ManifestStore.Version, manifest.SchemaVersion);
}

[Fact]
public async Task The_disclosure_is_rebuilt_from_edits_json_on_every_reseal()
{
    string sessionId = await SessionWithInsertionAsync("Typo.", 1_000);
    await RevertInsertionAsync(sessionId, "i000001");
    var manifest = await ManifestBuilder.BuildAsync(_paths, sessionId, TranscriptVersions.Root,
        _now, null, false, default);

    Assert.Empty(manifest.InsertedLines);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ManifestBuilderTests" -v q`
Expected: FAIL — `SessionManifest.InsertedLines` does not exist.

- [ ] **Step 3: Add the disclosure to the model**

In `src/LocalScribe.Core/Model/SessionManifest.cs`, add above `SessionManifest`:

```csharp
/// <summary>One human-authored transcript line, disclosed in the seal (design 2026-08-11 section
/// 2.3). The established pattern for declaring content inside an evidentiary artefact that the
/// machine did not originate, alongside ManifestFile.FabricatedSilence.
///
/// The TEXT is deliberately not repeated here: edits.json is in the same folder, is listed in
/// Files with its SHA-256, and travels verbatim in the .zip bundle, so the content is already
/// sealed. What this adds is discoverability - a reader can see that human-authored lines exist,
/// and exactly when and where, without parsing an overlay.</summary>
public sealed record InsertedLineRef
{
    public string Id { get; init; } = "";
    public long StartMs { get; init; }
    public DateTimeOffset AddedAtUtc { get; init; }
}
```

And to `SessionManifest`:

```csharp
    /// <summary>Every human-authored line in THIS version's edits.json (design 2026-08-11 section
    /// 2.3), rebuilt from that file on every seal.
    ///
    /// SchemaVersion is deliberately NOT bumped for this member. ManifestStore.ReadAsync rejects a
    /// manifest stamped newer than it supports, and a seal that cannot be read is what
    /// IntegrityReport treats as unsealed - so a bump would turn every untampered session into a
    /// false tamper verdict on an older build. LocalScribeJson sets no UnmappedMemberHandling, so
    /// the default Skip applies and an older build simply ignores this.
    ///
    /// No "Known" tri-state either, unlike ManifestFile.FabricatedSilence. That distinction exists
    /// because a leg's fabricated ranges genuinely can be unknown (imported audio, crash recovery,
    /// an older writer). There is no equivalent here: an older build's manifest lacking this member
    /// reads as an empty list, which is TRUE - that build cannot create an insertion at all.</summary>
    public IReadOnlyList<InsertedLineRef> InsertedLines { get; init; } = [];
```

- [ ] **Step 4: Compose it in `ManifestBuilder`**

In `src/LocalScribe.Core/Storage/ManifestBuilder.cs`, inside `BuildAsync`, read the version's
insertions and include them in the returned `SessionManifest` (add `using LocalScribe.Core.Model;`
if it is not already present — it is, via `ManifestFile`):

```csharp
        // Human-authored lines (design 2026-08-11 section 2.3), read from the version's own
        // edits.json - the same file already hashed above. Static read rather than an EditStore
        // instance: this class has no TimeProvider and must not construct a writer to do a read.
        var insertions = await EditStore.ReadInsertionsAsync(
            paths.EditsJson(sessionId, versionId), ct);
        var insertedLines = insertions
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new InsertedLineRef
            {
                Id = kv.Key, StartMs = kv.Value.StartMs, AddedAtUtc = kv.Value.AddedAtUtc,
            })
            .ToList();
```

Then add `InsertedLines = insertedLines,` to the `SessionManifest` initializer at the end of the
method.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ManifestBuilderTests" -v q`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2655 passed, 0 failed** (2651 + 4 new). Report the absolute numbers.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Model/SessionManifest.cs src/LocalScribe.Core/Storage/ManifestBuilder.cs tests/LocalScribe.Core.Tests/ManifestBuilderTests.cs
git commit -m "feat(manifest): disclose human-authored lines in the integrity seal"
```

---

### Task 7: The App write path

**Files:**
- Modify: `src/LocalScribe.App/Services/TranscriptEditBatch.cs`
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs:243-279`
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs` (locate the file covering
  `SaveTranscriptEditsAsync` with `grep -rln "SaveTranscriptEditsAsync" tests/`)

**Interfaces:**
- Consumes: `EditStore.ApplyInsertionsAsync`, `EditStore.InsertedLineWrite` (Task 2).
- Produces:
  - `InsertedLineEdit(string? InsertionId, string Text, long StartMs, string? SpeakerParticipantId)`
  - `TranscriptEditBatch` gains `IReadOnlyList<InsertedLineEdit> Insertions` and
    `IReadOnlyCollection<string> InsertionReverts` as **trailing positional members with defaults**.

`TranscriptEditBatch` is a positional record constructed in production and tests; append the two
legs last with `= []` defaults so every existing 4-argument construction compiles unchanged.

**Trap:** `SpeakerDetectionStep.MarkAsync` (`src/LocalScribe.App/Services/SpeakerDetectionStep.cs:184-200`)
is a production path that rewrites two sealed files and **deliberately** skips
`RegenerateProjectionsAsync`, calling `ResealAsync` directly. "Everything goes through the reseal
choke point" is false in general. This task's write path must be verified to reach
`RegenerateProjectionsAsync` explicitly — Step 1's test does that, do not assume it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task SaveTranscriptEdits_writes_an_insertion_and_regenerates_once()
{
    string sessionId = await FinalizedSessionAsync();
    var batch = new TranscriptEditBatch(new Dictionary<int, string>(), [], [], [])
    {
        Insertions = [new InsertedLineEdit(null, "Off-mic answer.", 50_000, "p-1")],
    };

    Assert.True(await _maintenance.SaveTranscriptEditsAsync(sessionId, batch, "v1", default));

    var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(default);
    var only = Assert.Single(edits!.Insertions);
    Assert.Equal("i000001", only.Key);
    Assert.Equal("Off-mic answer.", only.Value.Text);
    // The reseal choke point is NOT universal (SpeakerDetectionStep.MarkAsync skips it), so this
    // path's regeneration is asserted rather than assumed - and asserted to happen exactly once.
    Assert.Equal(1, _regenerationCount);
}

[Fact]
public async Task SaveTranscriptEdits_updates_an_existing_insertion()
{
    string sessionId = await FinalizedSessionAsync();
    var empty = new Dictionary<int, string>();
    await _maintenance.SaveTranscriptEditsAsync(sessionId,
        new TranscriptEditBatch(empty, [], [], [])
        { Insertions = [new InsertedLineEdit(null, "Typo.", 1_000, null)] }, "v1", default);

    await _maintenance.SaveTranscriptEditsAsync(sessionId,
        new TranscriptEditBatch(empty, [], [], [])
        { Insertions = [new InsertedLineEdit("i000001", "Fixed.", 2_000, null)] }, "v1", default);

    var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(default);
    Assert.Equal("Fixed.", Assert.Single(edits!.Insertions).Value.Text);
}

[Fact]
public async Task SaveTranscriptEdits_reverts_an_insertion()
{
    string sessionId = await FinalizedSessionAsync();
    var empty = new Dictionary<int, string>();
    await _maintenance.SaveTranscriptEditsAsync(sessionId,
        new TranscriptEditBatch(empty, [], [], [])
        { Insertions = [new InsertedLineEdit(null, "Typo.", 1_000, null)] }, "v1", default);

    Assert.True(await _maintenance.SaveTranscriptEditsAsync(sessionId,
        new TranscriptEditBatch(empty, [], [], []) { InsertionReverts = ["i000001"] }, "v1", default));

    var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(default);
    Assert.Empty(edits!.Insertions);
}

[Fact]
public async Task SaveTranscriptEdits_with_an_empty_insertion_batch_stays_a_no_op()
{
    string sessionId = await FinalizedSessionAsync();
    Assert.False(await _maintenance.SaveTranscriptEditsAsync(sessionId,
        new TranscriptEditBatch(new Dictionary<int, string>(), [], [], []), "v1", default));
    Assert.Equal(0, _regenerationCount);
}

[Fact]
public async Task An_insertion_and_a_correction_in_one_batch_write_once_and_regenerate_once()
{
    string sessionId = await FinalizedSessionAsync();
    var batch = new TranscriptEditBatch(
        new Dictionary<int, string> { [0] = "Corrected machine text." }, [], [], [])
    {
        Insertions = [new InsertedLineEdit(null, "Off-mic answer.", 50_000, null)],
    };

    Assert.True(await _maintenance.SaveTranscriptEditsAsync(sessionId, batch, "v1", default));

    var edits = await new EditStore(_paths.SessionDir(sessionId), _time).LoadAsync(default);
    Assert.Single(edits!.Corrections);
    Assert.Single(edits.Insertions);
    Assert.Equal(2, edits.SchemaVersion);
    Assert.Equal(1, _regenerationCount);
}
```

If the App test harness has no regeneration counter, count `manifest.json`'s `WrittenAtUtc` changes
or add a counting fake at the seam the existing tests already use for `SessionWriter`. Say in your
report which mechanism you used.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SaveTranscriptEdits" -v q`
Expected: FAIL — `InsertedLineEdit` does not exist.

- [ ] **Step 3: Add the batch legs**

In `src/LocalScribe.App/Services/TranscriptEditBatch.cs`, add above `TranscriptEditBatch`:

```csharp
/// <summary>One human-authored line in an edit batch (design 2026-08-11 section 2.3). A null
/// InsertionId MINTS a new line; a non-null one updates that existing line. Minting is deliberately
/// NOT done here: two expanded sections can each add a line in the same batch, and only
/// EditStore.ApplyInsertionsAsync - running under the per-session gate with the persisted map in
/// hand - can allocate ids that cannot collide.
/// SpeakerParticipantId is a meta.json participant id or null; never a clusterKey.</summary>
public sealed record InsertedLineEdit(string? InsertionId, string Text, long StartMs,
    string? SpeakerParticipantId);
```

Then extend `TranscriptEditBatch`. The two new legs are **body properties, not positional
parameters**: a positional parameter's default must be a compile-time constant, so a non-nullable
collection default is impossible there, and a nullable one would put a `CS8602` in every consumer
and test - which the warning baseline forbids. Body properties with `= []` are non-nullable, keep
every existing 4-argument construction compiling untouched, and are set with an object initializer.

```csharp
public sealed record TranscriptEditBatch(
    IReadOnlyDictionary<int, string> Corrections,
    IReadOnlyCollection<int> CorrectionReverts,
    IReadOnlyList<SplitEdit> Splits,
    IReadOnlyCollection<int> SplitReverts)
{
    /// <summary>Human-authored lines (design 2026-08-11 section 2.3) - the THIRD overlay, riding
    /// the same batch so one save is one edits.json write and one projection regeneration.
    /// A body property rather than a fifth positional parameter: a positional default must be a
    /// compile-time constant, so the only positional option is a nullable collection, and that
    /// would put a null-dereference warning in every consumer. Every existing four-argument
    /// construction keeps compiling untouched.</summary>
    public IReadOnlyList<InsertedLineEdit> Insertions { get; init; } = [];

    /// <summary>Insertion ids to REVERT (design 2026-08-11 section 2.3). Reverting a human act,
    /// the same shape as SplitReverts above - never a deletion of machine output.</summary>
    public IReadOnlyCollection<string> InsertionReverts { get; init; } = [];
}
```

Every construction in this plan's tests and in Tasks 9 and 10 therefore uses the object-initializer
form:

```csharp
new TranscriptEditBatch(corrections, [], splits, splitReverts)
{
    Insertions = insertions, InsertionReverts = insertionReverts,
}
```

- [ ] **Step 4: Handle the legs in `SaveTranscriptEditsAsync`**

In `src/LocalScribe.App/Services/MaintenanceService.cs`, inside the `RunForSessionAsync` lambda,
after the splits loop (`:271`) and **before** the `if (wrote)` regeneration (`:273`):

```csharp
            // Human-authored lines (design 2026-08-11 section 2.3). Last, so a batch that both
            // corrects a machine line and inserts a new one still produces ONE edits.json write per
            // overlay and ONE regeneration below. The session's duration is the insertion bound;
            // EditStore skips the upper bound when it is 0 (see ApplyInsertionsAsync's doc).
            if (batch.Insertions.Count > 0 || batch.InsertionReverts.Count > 0)
                wrote |= await store.ApplyInsertionsAsync(
                    batch.Insertions
                        .Select(i => new EditStore.InsertedLineWrite(
                            i.InsertionId, i.Text, i.StartMs, i.SpeakerParticipantId))
                        .ToList(),
                    batch.InsertionReverts, session.DurationMs, inner);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SaveTranscriptEdits" -v q`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2660 passed, 0 failed** (2655 + 5 new). Report the absolute numbers.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.App/Services/TranscriptEditBatch.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/
git commit -m "feat(app): route human-authored lines through SaveTranscriptEditsAsync"
```

---

### Task 8: Edit-mode view models

The highest-risk task in the plan. Every existing `GroupBy(s => s.Seq)` in the editor VMs will
mis-group inserted segments, because an inserted `RowSegment` carries `Seq = 0` and **machine seqs
also start at 0**.

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadSegment.cs:19`
- Modify: `src/LocalScribe.App/ViewModels/EditableSegmentViewModel.cs`
- Modify: `src/LocalScribe.App/ViewModels/EditableSectionViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/EditableSectionViewModelTests.cs`

**Interfaces:**
- Consumes: `RowSegment.InsertionId` (Task 3); `InsertedLineEdit` (Task 7).
- Produces:
  - `EditableSegmentViewModel`: `bool IsInserted`, `string? InsertionId`, and a constructor overload
    `EditableSegmentViewModel.ForInsertion(string? insertionId, string text, long startMs, SpeakerChoice? speaker, IReadOnlyList<SpeakerChoice> choices)`
  - `EditableSectionViewModel.AddInsertedLine(long startMs, IReadOnlyList<SpeakerChoice> choices)`
  - `EditableSectionViewModel.RemoveInsertedLine(EditableSegmentViewModel seg)`
  - `EditableSectionViewModel.CollectInsertions() -> IReadOnlyList<InsertedLineEdit>`
  - `EditableSectionViewModel.CollectInsertionReverts() -> IReadOnlyCollection<string>`

`InsertionId == null` on an `IsInserted` segment means "pending, not yet persisted".

**The complete list of seq-keyed groupings that must exclude inserted segments** (verified on
`a95fe04`):

| Site | Line |
|---|---|
| `EditableSectionViewModel.CollectSplits` | `:222` |
| `EditableSectionViewModel.CollectCorrections` | `:236-237` |
| `EditableSectionViewModel.Reindex` | `:253-254` |
| `EditableSectionViewModel.RevertSplit` | `:194`, `:205`, `:209` |
| `EditableSectionViewModel.SplitSegment` | `:167-182` (must refuse an inserted segment) |
| `ReadViewViewModel.HasUnsavedEdits` | `:171-173` (Task 9) |
| `ReadViewViewModel.SaveEditsAsync` pin loop | `:946` (Task 9) |

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void BeginEdit_materializes_an_inserted_segment_as_inserted()
{
    var section = new EditableSectionViewModel(RowWith(
        MachineSeg(seq: 0, "Machine text."),
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
    // Machine seq starts at 0 and an inserted segment's placeholder Seq is 0, so an unguarded
    // GroupBy would emit a "correction" for machine seq 0 carrying the inserted text.
    var section = EditingSection(MachineSeg(0, "Machine text."), InsertedSeg("i000001", 50_000, "Human text."));
    section.Segments.Single(s => s.IsInserted).EditedText = "Human text, edited.";

    Assert.Empty(section.CollectCorrections());
}

[Fact]
public void An_inserted_segment_is_never_collected_as_a_split()
{
    var section = EditingSection(MachineSeg(0, "Machine text."), InsertedSeg("i000001", 50_000, "Human."));
    Assert.Empty(section.CollectSplits());
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
    section.AddInsertedLine(startMs: 5_000, choices: []);
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
    section.AddInsertedLine(5_000, []);
    Assert.Empty(section.CollectInsertions());
}

[Fact]
public void RemoveInsertedLine_reverts_a_persisted_line_and_just_drops_a_pending_one()
{
    var section = EditingSection(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human."));
    section.AddInsertedLine(5_000, []);
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
public void ReadSegment_marks_an_inserted_start_as_estimated()
{
    var seg = new ReadSegment(new RowSegment(0, TranscriptSource.System, 50_000, 50_000,
        "Human.", "Human.", false, false, InsertionId: "i000001"));
    Assert.True(seg.IsEstimatedStart);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionViewModelTests" -v q`
Expected: FAIL — `IsInserted`, `AddInsertedLine`, `CollectInsertions` do not exist.

- [ ] **Step 3: Mark an inserted start estimated in read view**

`src/LocalScribe.App/ViewModels/ReadSegment.cs:19`:

```csharp
    /// <summary>True when this segment's start is a HUMAN ESTIMATE rather than a machine
    /// measurement: a split child's derived boundary, or a whole human-authored line (design
    /// 2026-08-11 section 2.3), whose timestamp is estimated by definition. Drives the
    /// "~[stamp] (estimated)" tooltip in SegmentText. App-side only: the exported document does
    /// NOT carry a per-line estimate mark - that is a per-turn mark, the category Part 3 deleted
    /// outright when it removed "[text corrected]".</summary>
    public bool IsEstimatedStart => Data.IsSplitChild || Data.InsertionId is not null;
```

- [ ] **Step 4: Add insertion state to `EditableSegmentViewModel`**

Add two members and a factory. Keep the existing constructor's signature untouched — every current
call site must compile unchanged — and add `isInserted`/`insertionId` as trailing optional
parameters:

```csharp
    /// <summary>True for a HUMAN-AUTHORED line (design 2026-08-11 section 2.3). When set, Seq,
    /// PartIndex and Source are placeholders and MUST NOT be read: Seq is 0 and machine seqs also
    /// start at 0, so every seq-keyed grouping in this VM partitions on this flag FIRST.</summary>
    public bool IsInserted { get; }

    /// <summary>The edits.json insertions key, or null for a line added in THIS edit session and
    /// not yet persisted - the batch carries null so EditStore mints the id under the per-session
    /// gate. Meaningless when IsInserted is false.</summary>
    public string? InsertionId { get; }
```

```csharp
    /// <summary>A human-authored sub-row (design 2026-08-11 section 2.3). DerivedStart is always
    /// true - a human-estimated timestamp - so the existing "(estimated)" field lights up with no
    /// new XAML state. Seq 0 / PartIndex 0 / Source System are placeholders, exactly as on the
    /// RowSegment this mirrors.</summary>
    public static EditableSegmentViewModel ForInsertion(string? insertionId, string text,
        long startMs, SpeakerChoice? speaker, IReadOnlyList<SpeakerChoice> choices)
        => new(seq: 0, TranscriptSource.System, partIndex: 0, text, startMs, endMs: startMs,
            derivedStart: true, rawText: text, speaker: speaker, isSplitChild: false, choices,
            isInserted: true, insertionId: insertionId);
```

- [ ] **Step 5: Guard and extend `EditableSectionViewModel`**

Add the pending state near `_splitReverts`:

```csharp
    private readonly HashSet<string> _insertionReverts = new(StringComparer.Ordinal);
```

Materialize inserted segments in `BeginEdit`'s loop — replace the body with a branch:

```csharp
        foreach (var s in Row.Segments)
        {
            var choices = ChoicesFor(s.Source);
            if (s.InsertionId is { } insertionId)
            {
                // A human-authored line: no machine seq to inherit, no pin store to consult, and
                // its speaker is a meta.json participant id carried on the line itself - never a
                // clusterKey, so it is matched against the participant leg of the choice list only.
                Segments.Add(EditableSegmentViewModel.ForInsertion(insertionId, s.ProjectedText,
                    s.StartMs, InsertedSpeaker(s, choices), choices));
                continue;
            }
            var speaker = s.IsSplitChild
                ? SplitChildSpeaker(s, choices)
                : currentSpeaker?.Invoke(s.Seq, s.Source, choices);
            Segments.Add(new EditableSegmentViewModel(s.Seq, s.Source, s.PartIndex,
                s.ProjectedText, s.StartMs, s.EndMs, derivedStart: s.PartIndex > 0, s.RawText,
                speaker: speaker, isSplitChild: s.IsSplitChild, choices));
        }
```

Task 3 carries the participant id through onto the inserted `RowSegment`, so the pre-selection is a
TARGET match, never a display-string match:

```csharp
    // The inserted line's own speaker, matched by TARGET (participant id) not display text, so a
    // Session Details rename keeps the selection instead of blanking it. Null when the line is
    // Unattributed or its participant is no longer an offered candidate - the same
    // no-speculative-attribution rule SplitChildSpeaker follows. Deliberately NOT routed through
    // the clusterKey leg: an inserted line has no diarisation cluster.
    private static SpeakerChoice? InsertedSpeaker(RowSegment s, IReadOnlyList<SpeakerChoice> choices)
        => s.SpeakerParticipantId is { } pid
            ? choices.FirstOrDefault(c => c.ParticipantId == pid)
            : null;
```

Add one more test to Step 1's set:

```csharp
[Fact]
public void An_inserted_segment_preselects_its_participant_by_target()
{
    var jane = new SpeakerChoice("Jane Okafor", "p-1", null);
    var section = new EditableSectionViewModel(RowWith(
        InsertedSeg("i000001", 50_000, "Human.", participantId: "p-1")));
    section.BeginEdit("relative", DateTimeOffset.UnixEpoch, remoteChoices: [jane], localChoices: [jane]);

    Assert.Same(jane, section.Segments.Single(s => s.IsInserted).Speaker);
}
```

Guard the four seq-keyed members:

```csharp
    // Human-authored segments carry a PLACEHOLDER Seq of 0 and machine seqs also start at 0, so
    // every seq-keyed grouping below partitions them out FIRST. Without this, a section holding an
    // inserted line would emit a phantom correction or split against machine seq 0.
    private static bool IsMachine(EditableSegmentViewModel s) => !s.IsInserted;
```

- `CollectSplits`: `Segments.Where(IsMachine).GroupBy(s => s.Seq)`
- `CollectCorrections`: `Segments.Where(s => IsMachine(s) && !s.IsSplitChild)`
- `Reindex`: build `countBySeq` and iterate over machine segments only; leave inserted segments'
  positions and `PartIndex` untouched.
- `RevertSplit`: filter `parts` with `IsMachine`, and change the removal loop to
  `if (IsMachine(Segments[i]) && Segments[i].Seq == seq) Segments.RemoveAt(i);`
- `SplitSegment`: `if (seg.IsInserted) return;` as the first statement, with the comment
  "a human-authored line has no machine segment to partition."

Add the three new members:

```csharp
    /// <summary>Add a human-authored sub-row to this section (design 2026-08-11 section 2.3), the
    /// edit-mode "+" affordance. Appended at the end of the section, which is the boundary the "+"
    /// sits on; the projection re-sorts it into place on the next load, so its position here is
    /// provisional. Carries no id: EditStore mints one under the per-session gate, so two sections
    /// each adding a line in the same batch cannot collide.</summary>
    public void AddInsertedLine(long startMs, IReadOnlyList<SpeakerChoice> choices)
        => Segments.Add(EditableSegmentViewModel.ForInsertion(
            insertionId: null, text: "", startMs, speaker: null, choices));

    /// <summary>Remove a human-authored line. A PERSISTED line becomes a revert - the same shape as
    /// RemoveSplitAsync, reverting a human act rather than deleting machine evidence. A PENDING one
    /// (added in this session, never written) simply leaves the collection. Machine segments are
    /// refused outright: the no-deletion rule protects machine output absolutely.</summary>
    public void RemoveInsertedLine(EditableSegmentViewModel seg)
    {
        if (!seg.IsInserted) return;
        if (seg.InsertionId is { } id) _insertionReverts.Add(id);
        Segments.Remove(seg);
    }

    /// <summary>Insertions to persist: every human-authored sub-row with non-blank text that is
    /// either PENDING (no id yet) or whose text or start changed from what was loaded. A pending
    /// row left blank is silently dropped - clicking "+" and changing your mind must not write, and
    /// must not fail validation either.</summary>
    public IReadOnlyList<InsertedLineEdit> CollectInsertions()
        => Segments.Where(s => s.IsInserted && !string.IsNullOrWhiteSpace(s.EditedText))
            .Where(s => s.InsertionId is null
                || s.EditedText.Trim() != s.ProjectedText.Trim()
                || s.StartMs != LoadedStartFor(s.InsertionId))
            .Select(s => new InsertedLineEdit(s.InsertionId, s.EditedText.Trim(), s.StartMs,
                s.Speaker?.ParticipantId))
            .ToList();

    public IReadOnlyCollection<string> CollectInsertionReverts() => _insertionReverts.ToList();

    // The start this insertion was LOADED with, so an untouched line writes nothing. Falls back to
    // the live value (no persisted row => nothing to compare, and the id-null case above already
    // decided to write).
    private long LoadedStartFor(string insertionId)
        => Row.Segments.FirstOrDefault(s => s.InsertionId == insertionId)?.StartMs ?? long.MinValue;
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~EditableSectionViewModelTests" -v q`
Expected: PASS.

- [ ] **Step 7: Prove two of the guards are real**

Remove the `Where(IsMachine)` from `CollectCorrections` and re-run
`An_inserted_segment_is_never_collected_as_a_correction`. Expected: FAIL. Restore.
Then remove the guard from `Reindex` and re-run
`Reindex_never_relabels_an_inserted_segment_as_a_split_child`. Expected: FAIL. Restore. Confirm both
PASS again. Do not commit the temporary changes.

- [ ] **Step 8: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2673 passed, 0 failed** (2660 + 13 new). Report the absolute numbers.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ tests/LocalScribe.App.Tests/
git commit -m "feat(editor): materialize, add and revert human-authored lines in edit mode"
```

---

### AMENDMENT (Task 8 review, 2026-08-11): an inserted line's speaker must survive

Task 8's review found the plan reproducing, twice, the exact data-loss shape this file already
carries a fix for — the 2026-08-02 gold-edit smoke bug, where a split child's persisted speaker was
blanked on re-edit and then written back as null by the next collect.

**Defect A — an inserted line is only ever offered the REMOTE candidate list.**
`ChoicesFor(source)` is `source == Local ? _localChoices : _remoteChoices`, and an inserted segment
carries the placeholder `Source = TranscriptSource.System`. So `BeginEdit` hands `_remoteChoices` to
both `InsertedSpeaker` and the row's dropdown. A persisted inserted line attributed to a **local**
participant therefore re-materialises with no match, blanks, and does not even offer the correct
name; the user edits that line's text and `CollectInsertions` writes `s.Speaker?.ParticipantId` —
now null. The attribution is destroyed by an unrelated text edit. `RefreshSpeakerChoices` is worse:
it actively re-points such a line to the unassign entry on any mid-edit roster change, no reload
required.

The root error is mine: an inserted line has no audio stream and therefore **no side**, so it must
never be routed through a side-selecting helper at all.

```csharp
    /// <summary>The candidate list for a HUMAN-AUTHORED line: BOTH sides' named participants,
    /// because an inserted line has no audio stream and therefore no side. Routing its placeholder
    /// TranscriptSource.System through ChoicesFor silently yields the REMOTE list only, so a line
    /// attributed to a LOCAL participant blanks on re-edit and is then WIPED by the next
    /// CollectInsertions - the same shape as the 2026-08-02 gold-edit smoke bug this file already
    /// carries a fix for ten lines below. Exactly one leading no-override entry, not two.</summary>
    private IReadOnlyList<SpeakerChoice> InsertedChoices()
    {
        var list = new List<SpeakerChoice>();
        bool haveUnassign = false;
        foreach (var c in _remoteChoices.Concat(_localChoices))
        {
            if (c.IsUnassign)
            {
                if (haveUnassign) continue;
                haveUnassign = true;
            }
            list.Add(c);
        }
        return list;
    }
```

Use it in three places: `BeginEdit`'s inserted branch (for both `InsertedSpeaker` and the row's own
choices), `RefreshSpeakerChoices` (`seg.IsInserted ? InsertedChoices() : ChoicesFor(seg.Source)`),
and `AddInsertedLine` — which currently takes its list from the caller, so a newly added row can
show one list and the same row after save/reload shows another. Change its signature to
`AddInsertedLine(long startMs)` and have it call `InsertedChoices()` itself; update Task 11's
`AddInsertedLineCommand` accordingly (it no longer passes a choice list).

**Defect B — a speaker-only change on a persisted inserted line is never collected.**
`CollectInsertions`'s change-detection disjunction is `InsertionId is null || text changed || start
changed`. There is no speaker leg, though the payload it builds carries `ParticipantId`. A user who
re-opens edit, changes only the speaker on an existing inserted line, and saves gets nothing
written. Today the change lands by accident in `ReadViewViewModel.SaveEditsAsync`'s pin loop — as a
pin against **machine seq 0** — and once Task 9 correctly excludes inserted segments from that loop
it will be dropped silently and permanently.

`ReadViewViewModel.cs:159-161` already records this precedent in its own words: "a pure
re-attribution changes no text and creates no split... Missing it would let a whole session's
re-attribution close silently."

```csharp
            .Where(s => s.InsertionId is null
                || s.EditedText.Trim() != s.ProjectedText.Trim()
                || s.StartMs != LoadedStartFor(s.InsertionId)
                || s.Speaker?.ParticipantId != LoadedParticipantFor(s.InsertionId))
```

```csharp
    // The participant this insertion was LOADED with, so a pure re-attribution is collected and an
    // untouched line still writes nothing.
    private string? LoadedParticipantFor(string insertionId)
        => Row.Segments.FirstOrDefault(s => s.InsertionId == insertionId)?.SpeakerParticipantId;
```

**Defect C — a hard-won regression rationale was deleted.** The brief's Step 5 snippet omitted the
six-line comment in `BeginEdit` explaining why a split child is pre-selected from the part's own
override ("Blanking a split child here (the old behaviour) let a later CollectSplits write null and
WIPE the persisted per-part speaker on any re-edit (2026-08-02 gold-edit smoke)"). Nothing required
removing it — the inserted branch goes *above* the code it describes. In a repo whose memory of
data-loss bugs lives in exactly these comments, restore it.

**Tests this amendment requires:**

```csharp
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
```

**Also settled by the same review** (both one-liners, fold in):

- `Reindex_never_relabels_an_inserted_segment_as_a_split_child`'s first assertion is vacuous under
  the real break, because `Reindex`'s reconstruction does not forward `isInserted`, so
  `Where(s => s.IsInserted)` is empty and `Assert.All` passes on an empty sequence. Add
  `Assert.Equal(2, section.Segments.Count(s => s.IsInserted));` before it.
- `LoadedStartFor`'s comment claims it "falls back to the live value"; it returns `long.MinValue`,
  which forces the row to be collected. Say what it does: "no persisted row => treat as changed."

### Task 9: `ReadViewViewModel` — save legs, guards, editor factory

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs:164-183` (`HasUnsavedEdits`),
  `:917-958` (`SaveEditsAsync`), and near `:1053` (the editor factories)
- Test: `tests/LocalScribe.App.Tests/ReadViewViewModelTests.cs`

**Interfaces:**
- Consumes: `CollectInsertions`, `CollectInsertionReverts`, `AddInsertedLine`,
  `RemoveInsertedLine` (Task 8); `TranscriptEditBatch` insertion legs (Task 7).
- Produces: `ReadViewViewModel.CreateInsertLineEditor(int rowIndex) -> InsertLineViewModel?`
  (the VM type arrives in Task 10 — this task defines the factory's call and Task 10 fills the type,
  so implement Task 9 **after** Task 10's VM exists, or stub the factory here and wire it in Task 10.
  **Recommended: do Task 10 before this task's Step 5.**)

**Two bugs this task exists to prevent, both confirmed on `a95fe04`:**

1. `HasUnsavedEdits` (`:171-173`) groups `sec.Segments` by `Seq` and compares the group size to
   `sec.Row.Segments.Count(s => s.Seq == g.Key)`. An inserted segment (Seq 0) inflates the group for
   machine seq 0 and makes every session holding an inserted line read as permanently dirty,
   prompting on every close.
2. `SaveEditsAsync`'s pin loop (`:946-957`) iterates `sec.Segments.Where(x => !x.IsSplitChild)` —
   which includes inserted segments — and calls `RemoveSpeakerPinsAsync(..., [seg.Seq], ...)` or
   `SaveSpeakerPinsAsync(..., [seg.Seq], ...)`. That writes a **pin on machine seq 0** for a line
   that has no seq, silently re-attributing a real machine segment.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void HasUnsavedEdits_is_false_for_a_loaded_session_holding_an_inserted_line()
{
    // Inserted segments carry Seq 0 and so does machine seq 0; an unguarded group-size compare
    // makes every such session read as permanently dirty and prompt on every close.
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human.")));
    vm.EnterEdit();
    vm.EditSections[0].BeginEdit("relative", DateTimeOffset.UnixEpoch);

    Assert.False(vm.HasUnsavedEdits);
}

[Fact]
public void HasUnsavedEdits_is_true_after_adding_a_line()
{
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine.")));
    vm.EnterEdit();
    vm.EditSections[0].BeginEdit("relative", DateTimeOffset.UnixEpoch);
    vm.EditSections[0].AddInsertedLine(5_000);
    vm.EditSections[0].Segments[^1].EditedText = "Newly authored.";

    Assert.True(vm.HasUnsavedEdits);
}

[Fact]
public void HasUnsavedEdits_is_true_after_removing_an_inserted_line()
{
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human.")));
    vm.EnterEdit();
    var sec = vm.EditSections[0];
    sec.BeginEdit("relative", DateTimeOffset.UnixEpoch);
    sec.RemoveInsertedLine(sec.Segments.Single(s => s.IsInserted));

    Assert.True(vm.HasUnsavedEdits);
}

[Fact]
public async Task SaveEdits_never_pins_a_speaker_for_an_inserted_line()
{
    // The seq-0 collision again: an inserted segment reaching the pin loop would write a pin on a
    // REAL machine segment, silently re-attributing it.
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human.")));
    vm.EnterEdit();
    var sec = vm.EditSections[0];
    sec.BeginEdit("relative", DateTimeOffset.UnixEpoch);
    sec.Segments.Single(s => s.IsInserted).Speaker = new SpeakerChoice("Jane Okafor", "p-1", null);

    await vm.SaveEditsAsync(default);

    Assert.Empty(_maintenance.SavedPins);
    Assert.Empty(_maintenance.RemovedPins);
}

[Fact]
public async Task SaveEdits_carries_the_insertion_legs_into_the_batch()
{
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human.")));
    vm.EnterEdit();
    var sec = vm.EditSections[0];
    sec.BeginEdit("relative", DateTimeOffset.UnixEpoch);
    sec.Segments.Single(s => s.InsertionId == "i000001").EditedText = "Human, corrected.";
    sec.AddInsertedLine(5_000);
    sec.Segments[^1].EditedText = "Newly authored.";

    await vm.SaveEditsAsync(default);

    var batch = Assert.Single(_maintenance.SavedBatches);
    Assert.Equal(2, batch.Insertions.Count);
    Assert.Contains(batch.Insertions, i => i.InsertionId == "i000001" && i.Text == "Human, corrected.");
    Assert.Contains(batch.Insertions, i => i.InsertionId is null && i.Text == "Newly authored.");
}

[Fact]
public async Task SaveEdits_carries_an_insertion_revert()
{
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine."), InsertedSeg("i000001", 50_000, "Human.")));
    vm.EnterEdit();
    var sec = vm.EditSections[0];
    sec.BeginEdit("relative", DateTimeOffset.UnixEpoch);
    sec.RemoveInsertedLine(sec.Segments.Single(s => s.IsInserted));

    await vm.SaveEditsAsync(default);

    Assert.Equal(["i000001"], Assert.Single(_maintenance.SavedBatches).InsertionReverts);
}

[Fact]
public void CreateInsertLineEditor_seeds_the_clicked_rows_start()
{
    var vm = LoadedWith(RowWith(MachineSeg(0, "Machine.", startMs: 50_000)));
    var editor = vm.CreateInsertLineEditor(rowIndex: 0);

    Assert.NotNull(editor);
    Assert.Equal(50_000, editor!.StartMs);
}

[Fact]
public void CreateInsertLineEditor_returns_null_for_a_marker_row()
{
    var vm = LoadedWith(MarkerRow("Recording paused"));
    Assert.Null(vm.CreateInsertLineEditor(rowIndex: 0));
}
```

Extend the test double for `MaintenanceService` with `SavedBatches`, `SavedPins` and `RemovedPins`
capture lists if it does not already have them — check the file first.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests" -v q`
Expected: FAIL — the guards are absent and `CreateInsertLineEditor` does not exist.

- [ ] **Step 3: Guard `HasUnsavedEdits`**

In `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`, in the `HasUnsavedEdits` body:

```csharp
                if (sec.CollectSplitReverts().Count > 0) return true;   // a revert leaves no other trace
                if (sec.CollectInsertionReverts().Count > 0) return true;
                if (sec.CollectInsertions().Count > 0) return true;
                // Machine segments ONLY: an inserted segment carries a placeholder Seq of 0 and
                // machine seqs also start at 0, so an unguarded group-size compare inflates seq 0's
                // group and makes every session holding an inserted line read as permanently dirty.
                foreach (var g in sec.Segments.Where(s => !s.IsInserted).GroupBy(s => s.Seq))
                    if (g.Count() != sec.Row.Segments.Count(s => s.InsertionId is null && s.Seq == g.Key))
                        return true;                                     // a split made in THIS session
                foreach (var seg in sec.Segments)
                {
                    if (seg.IsInserted) continue;   // its dirtiness is decided by CollectInsertions above
                    if (seg.EditedText.Trim() != seg.ProjectedText.Trim()) return true;
                    if (!SameSpeakerTarget(seg.Speaker, seg.OriginalSpeaker)) return true;
                }
```

Extend the property's doc comment with a fifth bullet:

```
    /// - The insertion legs are checked through their own Collect* pair rather than through the
    ///   per-segment compare below, because a PENDING inserted row has no loaded baseline to
    ///   compare against and a removed one leaves no trace in Segments at all.
```

- [ ] **Step 4: Carry the legs and guard the pin loop in `SaveEditsAsync`**

Collect alongside the existing legs:

```csharp
        var insertions = new List<InsertedLineEdit>();
        var insertionReverts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sec in EditSections.Where(s => s.IsEditing))
        {
            foreach (var kv in sec.CollectCorrections()) corrections[kv.Key] = kv.Value;
            splits.AddRange(sec.CollectSplits());
            foreach (int seq in sec.CollectSplitReverts()) splitReverts.Add(seq);
            insertions.AddRange(sec.CollectInsertions());
            foreach (string id in sec.CollectInsertionReverts()) insertionReverts.Add(id);
        }
```

Pass them to the batch (object-initializer form — the two legs are body properties, not positional
parameters; see Task 7):

```csharp
            var batch = new TranscriptEditBatch(corrections, [], wholeSplits, splitReverts.ToList())
            {
                Insertions = insertions, InsertionReverts = insertionReverts.ToList(),
            };
```

Guard the pin loop:

```csharp
                foreach (var seg in sec.Segments.Where(x => !x.IsSplitChild && !x.IsInserted))
```

with the comment:

```csharp
                    // Inserted segments are excluded, not merely skipped by SameSpeakerTarget: their
                    // Seq is a placeholder 0 and machine seqs START at 0, so a pin written from one
                    // would silently re-attribute a REAL machine segment. Their speaker rides in the
                    // batch above, on the InsertedLineEdit itself, as a meta.json participant id.
```

- [ ] **Step 5: Add the editor factory**

Beside `CreateCorrectionEditor` (`:1053`):

```csharp
    /// <summary>Editor for the read view's "Add line here..." dialog (design 2026-08-11 section
    /// 2.3). Seeds the timestamp from the CLICKED row's start, which is what makes the gesture
    /// positional: an inserted line takes TranscriptProjection.InsertedRank and therefore lands
    /// immediately BEFORE that row. Null over a marker row - a marker is machine bookkeeping and
    /// carries no position a person is annotating.
    /// versionId is captured now, exactly as CreateCorrectionEditor does (F1 fix): the dialog is
    /// modal over this window, but a background re-transcription completing mid-dialog could
    /// otherwise redirect the write into a different version's overlay.</summary>
    public InsertLineViewModel? CreateInsertLineEditor(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count || _loadedMeta is null) return null;
        var row = Rows[rowIndex].Data;
        if (row.IsMarker) return null;
        return new InsertLineViewModel(_maintenance, DialogReporter, SessionId, row.StartMs,
            _loadedMeta, TimestampsMode, StartedAtLocal, _loadedVersionId, _loadedDurationMs);
    }
```

If `ReadViewViewModel` holds no loaded duration, add a `private long _loadedDurationMs;` field
stamped from `view.Session.DurationMs` in `Apply`/`ApplyRows` alongside the other `_loaded*` fields.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~ReadViewViewModelTests" -v q`
Expected: PASS.

- [ ] **Step 7: Prove the pin guard is real**

Remove `&& !x.IsInserted` from the pin loop and re-run
`SaveEdits_never_pins_a_speaker_for_an_inserted_line`. Expected: FAIL. Restore and confirm PASS.
Do not commit the temporary change.

- [ ] **Step 8: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2681 passed, 0 failed** (2673 + 8 new). Report the absolute numbers.

- [ ] **Step 9: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/
git commit -m "feat(readview): carry insertion legs through save and guard the seq-0 collisions"
```

---

### Task 10: The `Add line here...` dialog

**Files:**
- Create: `src/LocalScribe.App/ViewModels/InsertLineViewModel.cs`
- Create: `src/LocalScribe.App/InsertLineDialog.xaml`, `src/LocalScribe.App/InsertLineDialog.xaml.cs`
- Test: `tests/LocalScribe.App.Tests/InsertLineViewModelTests.cs`

**Interfaces:**
- Consumes: `MaintenanceService.SaveTranscriptEditsAsync` + `TranscriptEditBatch` insertion legs
  (Task 7); `SpeakerChoices.Build` (existing); `TimestampParser.TryParse` (existing, used by
  `ReadViewViewModel.GoToTimestamp:715`); `TimestampFormat.Stamp` (existing).
- Produces: `InsertLineViewModel` with `long StartMs`, `string TimestampText`, `string Text`,
  `IReadOnlyList<SpeakerChoice> SpeakerChoices`, `SpeakerChoice? Speaker`,
  `string ValidationMessage`, `bool IsSaving`, `Task<bool> SaveAsync(CancellationToken)`.

The dialog's speaker dropdown offers **named participants only, plus an "Unattributed" entry**. It
does **not** offer detected-voice cluster choices: `SpeakerChoices.Build` appends those with a
`ClusterKey`, and an inserted line's speaker is a participant id by design.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void The_dialog_seeds_the_clicked_rows_timestamp()
{
    var vm = NewVm(seedStartMs: 3_725_000, timestampsMode: "relative");
    Assert.Equal(3_725_000, vm.StartMs);
    Assert.Equal(TimestampFormat.Stamp(3_725_000, "relative", _startedAtLocal), vm.TimestampText);
}

[Fact]
public void The_speaker_list_offers_named_participants_and_Unattributed_but_no_clusters()
{
    var vm = NewVm(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)],
        speakers: SpeakersWithNamedCluster("Remote:1", "Detected voice 1"));

    Assert.Contains(vm.SpeakerChoices, c => c.Display == "Jane Okafor" && c.ParticipantId == "p-1");
    Assert.Contains(vm.SpeakerChoices, c => c.Display == InsertedLineLabels.Unattributed);
    Assert.DoesNotContain(vm.SpeakerChoices, c => c.ClusterKey is not null);
}

[Fact]
public void The_default_selection_is_Unattributed()
{
    var vm = NewVm(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
    Assert.Null(vm.Speaker!.ParticipantId);
    Assert.Equal(InsertedLineLabels.Unattributed, vm.Speaker.Display);
}

[Fact]
public async Task Save_rejects_blank_text_without_writing()
{
    var vm = NewVm();
    vm.Text = "   ";
    Assert.False(await vm.SaveAsync(default));
    Assert.NotEmpty(vm.ValidationMessage);
    Assert.Empty(_maintenance.SavedBatches);
}

[Fact]
public async Task Save_rejects_an_unparseable_timestamp_without_writing()
{
    var vm = NewVm();
    vm.Text = "Off-mic answer.";
    vm.TimestampText = "not a time";
    Assert.False(await vm.SaveAsync(default));
    Assert.NotEmpty(vm.ValidationMessage);
    Assert.Empty(_maintenance.SavedBatches);
}

[Fact]
public async Task Save_rejects_a_timestamp_past_the_session_duration_without_writing()
{
    var vm = NewVm(durationMs: 600_000, timestampsMode: "relative");
    vm.Text = "Off-mic answer.";
    vm.TimestampText = "00:11:00";
    Assert.False(await vm.SaveAsync(default));
    Assert.NotEmpty(vm.ValidationMessage);
    Assert.Empty(_maintenance.SavedBatches);
}

[Fact]
public async Task Save_writes_one_insertion_with_the_parsed_timestamp_and_participant()
{
    var vm = NewVm(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)],
        durationMs: 600_000, timestampsMode: "relative");
    vm.Text = "  Off-mic answer.  ";
    vm.TimestampText = "00:01:01";
    vm.Speaker = vm.SpeakerChoices.Single(c => c.ParticipantId == "p-1");

    Assert.True(await vm.SaveAsync(default));

    var line = Assert.Single(Assert.Single(_maintenance.SavedBatches).Insertions);
    Assert.Null(line.InsertionId);          // mint
    Assert.Equal("Off-mic answer.", line.Text);
    Assert.Equal(61_000, line.StartMs);
    Assert.Equal("p-1", line.SpeakerParticipantId);
}

[Fact]
public async Task Save_targets_the_version_the_dialog_was_opened_against()
{
    var vm = NewVm(versionId: "v2", durationMs: 600_000);
    vm.Text = "Off-mic answer.";
    Assert.True(await vm.SaveAsync(default));
    Assert.Equal("v2", _maintenance.SavedVersionIds.Single());
}

[Fact]
public async Task A_timestamp_at_exactly_the_session_duration_is_accepted()
{
    var vm = NewVm(durationMs: 600_000, timestampsMode: "relative");
    vm.Text = "Final word.";
    vm.TimestampText = "00:10:00";
    Assert.True(await vm.SaveAsync(default));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~InsertLineViewModelTests" -v q`
Expected: FAIL — `InsertLineViewModel` does not exist.

- [ ] **Step 3: Write the view model**

Create `src/LocalScribe.App/ViewModels/InsertLineViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

namespace LocalScribe.App.ViewModels;

/// <summary>Editor VM for the read view's "Add line here..." dialog (design 2026-08-11 section
/// 2.3): a person authoring a transcript line where the machine heard nothing. WPF-free, like
/// CorrectTextViewModel, and for the same reason - the App suite has no STA/dispatcher harness, so
/// anything decidable that lived in the dialog's code-behind would be permanently untestable.
///
/// The speaker list offers NAMED PARTICIPANTS ONLY plus Unattributed. Detected-voice clusters are
/// deliberately absent: an inserted line's speaker is a meta.json participant id, never a
/// clusterKey, because speakers.json maps diarisation clusters to names and a human-authored line
/// has no cluster.
///
/// The timestamp is human-estimated by definition and is parsed by TimestampParser - the same
/// parser the read view's go-to box uses, so an unpadded "1:02:03" cannot mean two different
/// instants in two places.</summary>
public sealed partial class InsertLineViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly IUiErrorReporter _reporter;
    private readonly string _versionId;
    private readonly string _timestampsMode;
    private readonly DateTimeOffset _startedAtLocal;
    private readonly long _durationMs;

    public string SessionId { get; }
    /// <summary>The seed from the clicked row, kept so a caller can assert what was seeded even
    /// after the user retypes TimestampText.</summary>
    public long StartMs { get; }
    public IReadOnlyList<SpeakerChoice> SpeakerChoices { get; }

    [ObservableProperty] private string _timestampText;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private SpeakerChoice? _speaker;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _validationMessage = "";

    public InsertLineViewModel(MaintenanceService maintenance, IUiErrorReporter reporter,
        string sessionId, long seedStartMs, SessionMeta meta, string timestampsMode,
        DateTimeOffset startedAtLocal, string versionId, long durationMs)
    {
        (_maintenance, _reporter, SessionId, _versionId) = (maintenance, reporter, sessionId, versionId);
        (_timestampsMode, _startedAtLocal, _durationMs) = (timestampsMode, startedAtLocal, durationMs);
        StartMs = seedStartMs;
        _timestampText = TimestampFormat.Stamp(seedStartMs, timestampsMode, startedAtLocal);

        // Named participants only. SpeakerChoices.Build also appends detected-voice clusters and a
        // leading "Automatic (Me / Them)" - neither belongs on a line with no audio stream and no
        // cluster behind it.
        var choices = new List<SpeakerChoice>
        {
            new(InsertedLineLabels.Unattributed, null, null),
        };
        foreach (var p in meta.Participants)
            if (p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name))
                choices.Add(new SpeakerChoice(p.Name, p.Id, null));
        SpeakerChoices = choices;
        _speaker = choices[0];
    }

    /// <summary>True = the line was written, close the dialog. False = stay open: a validation
    /// problem (ValidationMessage set) or an IO failure (reported via IUiErrorReporter).</summary>
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Text))
        {
            ValidationMessage = "Type the words that were said. A transcript line is never blank.";
            return false;
        }
        if (!TimestampParser.TryParse(TimestampText, _timestampsMode, _startedAtLocal, out long ms))
        {
            ValidationMessage = "That timestamp could not be read. Use the same format as the "
                + "transcript's own stamps.";
            return false;
        }
        if (ms < 0 || (_durationMs > 0 && ms > _durationMs))
        {
            ValidationMessage = "That time is outside this recording. Choose a time within the "
                + "session.";
            return false;
        }

        ValidationMessage = "";
        IsSaving = true;
        try
        {
            var batch = new TranscriptEditBatch(new Dictionary<int, string>(), [], [], [])
            {
                Insertions = [new InsertedLineEdit(null, Text.Trim(), ms, Speaker?.ParticipantId)],
            };
            await _maintenance.SaveTranscriptEditsAsync(SessionId, batch, _versionId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _reporter.Report("Add transcript line", ex);
            return false;
        }
        finally { IsSaving = false; }
    }
}
```

- [ ] **Step 4: Write the dialog shell**

Create `src/LocalScribe.App/InsertLineDialog.xaml`. Copy `CorrectTextDialog.xaml`'s window chrome,
resources and button row verbatim so the two dialogs are visually identical, and replace its body
with:

- a read-only label showing the session title (optional, only if `CorrectTextDialog` has one),
- a `TextBox` bound to `TimestampText` labelled `Time`,
- a `ComboBox` bound `ItemsSource="{Binding SpeakerChoices}"` `SelectedItem="{Binding Speaker}"`
  `DisplayMemberPath="Display"` labelled `Speaker`,
- a multi-line `TextBox` bound to `Text` (`AcceptsReturn="True"`, `TextWrapping="Wrap"`,
  `MinHeight="90"`) labelled `Words spoken`,
- a `TextBlock` bound to `ValidationMessage`, collapsed when empty (reuse whatever converter or
  style `CorrectTextDialog.xaml` already uses for its own validation text),
- a hint line: `This time is an estimate. It will show as "(estimated)" in the app.`

Window title: `Add line here`. Use `x:Class="LocalScribe.App.InsertLineDialog"`.

Create `src/LocalScribe.App/InsertLineDialog.xaml.cs` — a humble shell, exactly the
`CorrectTextDialog.xaml.cs` shape:

```csharp
using System.Windows;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Modal "Add line here..." dialog (design 2026-08-11 section 2.3). Humble shell: seeding,
/// timestamp parsing, validation and the gated save all live in InsertLineViewModel; this closes
/// with DialogResult=true only when the VM reports the write landed (the caller then reloads rows).</summary>
public partial class InsertLineDialog
{
    private readonly InsertLineViewModel _vm;

    public InsertLineDialog(InsertLineViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSaving) return;
        if (await _vm.SaveAsync(CancellationToken.None)) DialogResult = true;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~InsertLineViewModelTests" -v q`
Expected: PASS.

- [ ] **Step 6: Run the full gate**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2690 passed, 0 failed** (2681 + 9 new). Report the absolute numbers.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.App/ViewModels/InsertLineViewModel.cs src/LocalScribe.App/InsertLineDialog.xaml src/LocalScribe.App/InsertLineDialog.xaml.cs tests/LocalScribe.App.Tests/InsertLineViewModelTests.cs
git commit -m "feat(readview): add the Add line here dialog and its editor view model"
```

---

### Task 11: Wire both entry points into the window

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml.cs` (commands near `:79-142`, handler near `:595`)
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml:486-518` (context menu), and the edit-mode
  section template around `:690-720`
- Test: none automated — this layer is XAML and STA-bound, and this suite has no STA harness. Every
  decidable half is already covered by Tasks 8-10.

**Interfaces:**
- Consumes: `ReadViewViewModel.CreateInsertLineEditor` (Task 9); `InsertLineDialog` (Task 10);
  `EditableSectionViewModel.AddInsertedLine` / `RemoveInsertedLine` (Task 8).
- Produces: `ReadViewWindow.AddLineCommand` (`IAsyncRelayCommand<ReadRow>`),
  `ReadViewWindow.AddInsertedLineCommand` (`IRelayCommand<EditableSectionViewModel>`),
  `ReadViewWindow.RemoveInsertedLineCommand` (`IRelayCommand<EditableSegmentViewModel>`).

**Gotcha (Stage 6, recorded in this repo):** a `Style.Setter` `ContextMenu` **never** wires a
`Click=` handler. Route through the `WindowProxy` commands exactly as the six existing items do.
Never use `ElementName=Self` from inside a `Style.Setter` or an `InputBinding` — an `InputBinding`
is a `Freezable` in neither tree and the binding fails **silently**, which no test here can catch.

- [ ] **Step 1: Add the commands to the window**

In `src/LocalScribe.App/ReadViewWindow.xaml.cs`, beside the existing row commands (`:79-82`):

```csharp
    /// <summary>"Add line here..." (design 2026-08-11 section 2.3). On the WINDOW, like every other
    /// WindowProxy row command, because it opens a modal dialog. The decidable halves are elsewhere
    /// and are tested: ReadViewViewModel.CreateInsertLineEditor seeds it, InsertLineViewModel
    /// validates and writes.</summary>
    public IAsyncRelayCommand<ReadRow> AddLineCommand { get; }

    /// <summary>Edit mode's "+" affordance and its paired remove. On the window for the same reason
    /// the other Style.Setter-reachable commands are: a DataTemplate inside an ItemContainerStyle
    /// cannot bind Click= handlers, and ElementName resolution from there fails silently.</summary>
    public IRelayCommand<EditableSectionViewModel> AddInsertedLineCommand { get; }
    public IRelayCommand<EditableSegmentViewModel> RemoveInsertedLineCommand { get; }
```

And in the constructor, beside `:125-133` — close over the `vm` **parameter**, never the
not-yet-assigned `_vm` field:

```csharp
        AddLineCommand = new AsyncRelayCommand<ReadRow>(AddLineAsync);
        AddInsertedLineCommand = new RelayCommand<EditableSectionViewModel>(
            // Seed at the section's end, which is the boundary the "+" sits on. The projection
            // re-sorts on the next load; this position is provisional.
            // NO choice list is passed: an inserted line has no audio stream and therefore no side,
            // so the section builds its own both-sides list via InsertedChoices() (Task 8
            // amendment). Passing one side's list here is what silently wiped a locally-attributed
            // line's speaker.
            section => section?.AddInsertedLine(
                section.Segments.Count > 0 ? section.Segments[^1].StartMs : section.Row.StartMs));
        RemoveInsertedLineCommand = new RelayCommand<EditableSegmentViewModel>(
            seg => { if (seg is not null) SectionOf(seg)?.RemoveInsertedLine(seg); });
```

The command passes **no** speaker list — `AddInsertedLine(long startMs)` builds its own from both
sides via `InsertedChoices()` (Task 8 amendment), because an inserted line has no audio stream and
therefore no side. Do not reintroduce a `SpeakerChoicesForSource(...)` argument here: passing one
side's list is exactly what silently wiped a locally-attributed line's speaker.

Add a small `SectionOf(EditableSegmentViewModel)` helper on the window that scans
`_vm.EditSections` for the section containing the segment.

Add the handler beside `CorrectTextAsync` (`:595`):

```csharp
    private async Task AddLineAsync(ReadRow? row)
    {
        if (row is null) return;
        var editor = _vm.CreateInsertLineEditor(_vm.Rows.IndexOf(row));
        if (editor is null) return;
        var dialog = new InsertLineDialog(editor) { Owner = this };
        _vm.ClearStatus();          // no stale message sitting under a fresh attempt (T1-5)
        if (dialog.ShowDialog() == true) await ReloadPreservingScrollAsync();
    }
```

- [ ] **Step 2: Add the seventh context-menu item**

In `src/LocalScribe.App/ReadViewWindow.xaml`, after the `Remove speaker pin...` item (`:514-517`) and
inside the same `<ContextMenu>`:

```xml
                                <Separator />
                                <!-- Design 2026-08-11 section 2.3: author a line where the machine
                                     heard nothing. Enabled on any non-marker row (the opening
                                     handler already suppresses the whole menu over markers) - unlike
                                     the editing items above it does NOT gate on HasSegments,
                                     because it creates content rather than altering existing
                                     content. Routed through WindowProxy like every other item here:
                                     a Style.Setter ContextMenu never wires a Click= handler. -->
                                <MenuItem Header="Add line here..."
                                          Command="{Binding Data.AddLineCommand, Source={StaticResource WindowProxy}}"
                                          CommandParameter="{Binding}" />
```

- [ ] **Step 3: Add the edit-mode `+` and remove affordances**

In the edit-mode section template (near `:690-720`, the block that already renders the
`(estimated)` label for split children):

1. A `Button` at the end of each expanded section's segment list, `Content="+ Add line"`, styled as
   a subtle text button consistent with the surrounding chrome:
   ```xml
   <Button Content="+ Add line"
           Command="{Binding Data.AddInsertedLineCommand, Source={StaticResource WindowProxy}}"
           CommandParameter="{Binding}"
           ToolTip="Add a line a person heard but the recording did not capture."
           HorizontalAlignment="Left" Margin="0,4,0,0" />
   ```
   Bind its visibility to the section's `IsEditing` exactly as the surrounding edit chrome does.

2. A `Remove` button on each **inserted** sub-row only, bound to `RemoveInsertedLineCommand` with
   `CommandParameter="{Binding}"`, and made visible by a `DataTrigger` on the segment's `IsInserted`
   (default `Collapsed`). Comment it:
   ```xml
   <!-- Only ever offered on a human-authored line. Removing one reverts a HUMAN act, the same
        shape as reverting a split; machine output is never deletable (spec section 1.6). -->
   ```

3. The `(estimated)` label at `:697` already lights up from `DerivedStart`, and
   `EditableSegmentViewModel.ForInsertion` sets `derivedStart: true`, so an inserted sub-row picks it
   up with **no XAML change**. Verify this by reading the binding at `:690-700` and confirming it
   binds `DerivedStart` (not `IsSplitChild`); if it binds `IsSplitChild`, change it to
   `DerivedStart` and note the change in your report.

- [ ] **Step 4: Build and run the full gate**

Run: `dotnet build LocalScribe.slnx -c Debug` then
`dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: **2690 passed, 0 failed** (no new tests — this task is XAML). Report the absolute numbers
and confirm the `CS8602` count is still exactly 7. A XAML binding error will not fail the build; it
is caught in Task 12's manual check.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs
git commit -m "feat(readview): wire Add line here and the edit-mode + affordance"
```

---

### Task 12: Spec audit, both gates, and the manual check

**Files:**
- Modify: `docs/specs/localscribe-specs.md`
- Modify: `docs/superpowers/specs/2026-08-11-import-resilience-line-insertion-lean-export-design.md`
  (§2 corrections only — see below)

Part 3's plan named six passages needing updates and its implementer found four more. Budget for
this audit being incomplete too.

- [ ] **Step 1: Find every passage that this branch made stale**

```bash
grep -n "edits\.json\|Corrections\|Splits\|RowSegment\|SearchLine\|McpTranscriptRowDto\|HumanLayerCounts\|human layer\|manifest\.json\|schemaVersion\|SchemaVersion\|no-deletion\|never deleted\|SourceRank\|sort key" docs/specs/localscribe-specs.md
```

Open every hit. For each, decide whether Part 2 changed what it claims. At minimum these are known
to be stale:

- The `edits.json` shape — it now holds three overlays, not two.
- The `edits.json` schema version — 1 or 2, and why it is conditional.
- The projection row model — `RowSegment.InsertionId`, and the rule that `Seq`/`PartIndex`/`Source`
  are meaningless when it is set.
- The row sort key — now four keys, and inserted rows precede machine rows at an identical start.
- The human layer / disclaimer — now five human counts, and an insertion selects the edited variant.
- `manifest.json` — now carries `insertedLines`, at schema version 1.
- Any passage asserting that transcript content is only ever machine-produced, or that every
  displayed line traces to a `transcript.jsonl` line.
- Any passage asserting that "everything goes through `RegenerateProjectionsAsync`" — still false in
  general (`SpeakerDetectionStep.MarkAsync`), and now with a second write path relying on it.

Write the changes. British spelling. Do not restructure sections you are not correcting.

- [ ] **Step 2: Correct the design spec's own §2 anchors**

The design spec's §2 was written before Parts 1 and 3 landed and is now wrong in five places. Fix
them in place, each as a one-line correction with a short note, so a future reader is not sent to a
dead anchor:

1. `:250-252` — the count-arithmetic list says **five** sites. It is **six**: Part 1 added
   `AudioImporter.cs:521-522`. Current verified lines: `SessionWriter.cs:151-152`,
   `OfflinePipelineRunner.cs:228-229`, `SessionController.cs:1888-1889`, `AudioImporter.cs:322-323`,
   `AudioImporter.cs:521-522`, `SpeakerDetectionStep.cs:196` (MarkerCount only).
2. `:211-212` — `TranscriptEditBatch` is in the **App** layer:
   `src/LocalScribe.App/Services/TranscriptEditBatch.cs:20-24`, not Core/Model.
3. `:283-284` — the citation problem does not exist. `TranscriptCitation.Format` takes a
   `DisplayRow` and its output carries no seq; `AssistantCitationFormat`/`CitationValidator` are
   timestamp-based. Replace the paragraph with a note that this round-trips with no new surface
   form, and cite the test added in Task 5.
4. `:263-265` — `VoiceprintEnrollmentService` is under **`People/`**, not `Voice/`. Full paths:
   `src/LocalScribe.Core/Diarisation/ClusterAssigner.cs:13`,
   `src/LocalScribe.Core/People/VoiceprintEnrollmentService.cs:182,208`,
   `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs:210,394,416`.
5. `:296-299` — the manifest provenance paragraph should record that `insertedLines` needs **no**
   tri-state and **no** schema bump, with the reasons from Task 6.

- [ ] **Step 3: Run both gates**

```bash
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```
Expected: **2690 passed, 0 failed**.

```bash
LOCALSCRIBE_MODELS=/tmp/localscribe-empty-models dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```
(On PowerShell: `$env:LOCALSCRIBE_MODELS = "$env:TEMP\localscribe-empty-models"` against a freshly
created empty directory.)
Expected: **2690 passed, 0 failed**.

Report both absolute numbers. If either differs, do not proceed — diagnose it.

- [ ] **Step 4: Confirm the warning baseline**

```bash
dotnet build LocalScribe.slnx -c Debug --no-incremental 2>&1 | grep -c "CS8602"
```
Expected: exactly **7**, all in `tests/LocalScribe.Core.Tests/DocxRendererTests.cs`. Any other
warning is a defect — fix it before proceeding.

- [ ] **Step 5: Confirm ASCII-only in `src/`**

There is no automated gate; check by hand:

```bash
grep -rPn "[^\x00-\x7F]" src/ --include=*.cs --include=*.xaml | grep -v "\.claude"
```
Expected: only pre-existing hits. Any new one introduced by this branch must be replaced with a
`\u` escape (the `ExportNotices` precedent) or plain ASCII.

- [ ] **Step 6: Manual check — the two entry points**

XAML binding failures are silent and no test in this suite can catch them. Build, close any running
`LocalScribe.App.exe` (it locks `Core.dll`), launch, and confirm on a real finalized session:

1. Read view -> right-click a speech row -> `Add line here...` opens the dialog with the row's
   timestamp seeded.
2. Type text, pick a speaker, Save. The line appears **immediately before** the row you clicked, with
   the chosen speaker's name.
3. Hover its timestamp: the tooltip reads `~[stamp] (estimated)`.
4. Right-click a **marker** row: the menu is suppressed (existing behaviour, unchanged).
5. Enter Edit mode. The inserted line appears as a sub-row with `(estimated)` and a Remove button;
   machine sub-rows have no Remove button.
6. Click `+ Add line`, type text, Save. Both lines survive.
7. Remove the first inserted line, Save. It is gone; the machine rows are untouched.
8. Export to `.docx`: the inserted line reads plainly in the body with **no** per-line mark, and the
   disclaimer is the "reviewed and corrected by a person" variant.
9. Open `manifest.json`: `insertedLines` lists the surviving line; `schemaVersion` is `1`.
10. Open `edits.json`: `schemaVersion` is `2` while an insertion exists.

Record the result of each of the ten in your report. A failure here is a defect in this branch, not
a smoke-test note.

- [ ] **Step 7: Commit**

```bash
git add docs/
git commit -m "docs: record the insertions overlay and correct the design spec's stale section 2 anchors"
```

---

## Verification checklist before merge

- [ ] `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — **2690 passed, 0 failed**,
      absolute numbers reported.
- [ ] The same gate with `LOCALSCRIBE_MODELS` pointed at a fresh empty directory — same numbers.
- [ ] Exactly 7 `CS8602` on a clean `--no-incremental` rebuild, all pre-existing.
- [ ] No new non-ASCII bytes under `src/`.
- [ ] All ten manual checks in Task 12 Step 6 pass.
- [ ] The three "prove the test is real" steps (Tasks 3, 4, 8, 9) were each run and each failed with
      the guard removed.
- [ ] `git log --oneline master..HEAD` shows one commit per task, none on `master`.

## Open items carried in, not this branch's to fix

Flag these in the final report; do not fix them here.

- **Part 3's Word smoke is still outstanding.** `OpenXmlValidator(Office2019)` is a schema check,
  not Word's corruption heuristic. Part 2 adds body rows rather than header structure, so it does
  not widen that risk — but the smoke covers both once run.
- **`master` is 12 commits ahead of `origin` and unpushed.**
- **`SpeakersMerge.cs:111-114`** re-keys a colliding cluster while carrying the old label verbatim,
  so `speakers.json` can hold `"Local:5" -> "Local Speaker 1"`, which
  `DefaultSpeakerLabels.IsMachineDefault` scores as human-named. Unreachable today, untested, and
  the disclaimer depends on that coupling.
- **`SpeakersMerge.ParseId:206-212`** parses the `"Source:id"` suffix with a bare `int.TryParse` —
  no `NumberStyles`, no `InvariantCulture`. `InsertedLineLabels.MintId` deliberately does not repeat
  this.
- **`ExportNotices.cs`'s doc comment claims a byte-scan gate exists.** It does not; the ASCII rule
  is a review convention (`CONTRIBUTING.md:118`).
- **The `Edits` audit is now complete for the disclaimer's purposes**: `Corrections`, `Splits` and
  `Insertions` are the three human acts `edits.json` holds, and all three reach
  `HumanLayerCounts`. `speakers.json` contributes `SpeakerPins` and `SpeakerNames`. If a fourth
  human act is ever added to either file, `DisclaimerFor` must gain it in the same change.

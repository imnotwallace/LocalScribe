# Transcript Editor Overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a Read⇄Edit table editor to the session read view with mid-segment splitting (a non-destructive `splits` overlay), inline assign-only speaker selection, and live roster sync from Session Details.

**Architecture:** A new `splits` overlay in `edits.json`, keyed by the immutable machine `seq`, partitions one segment into N human-authored children at projection time while the machine `transcript.jsonl` line stays untouched (revert restores the single segment). `TranscriptProjection.Build` expands a split seq into N `ProjectedSegment`s; everything downstream (dedup, grouping, exporters) consumes the projection unchanged. The UI is an Edit mode inside the existing `ReadViewWindow`: sections render read-only until clicked, then expand into per-segment sub-rows whose edit state lives entirely on view-models (data-triggered templates), so the virtualized list recycles safely.

**Tech Stack:** C# / .NET 10, WPF + WPF-UI (FluentWindow), CommunityToolkit.Mvvm, xUnit. Core is WPF-free and headless-testable.

## Global Constraints

- **Evidentiary firewall:** `transcript.jsonl` is NEVER mutated or rewritten. Corrections, splits, and pins are additive overlays keyed by the immutable `seq`. No deletion / hide / redaction anywhere. A split **partitions**; an empty/whitespace child is rejected. Revert restores the single original machine segment.
- **Derived timestamps flagged:** a split child's start time (except the first, which inherits the machine start) is human-derived and stored full-ms; the UI constrains edits to **10 ms** steps and marks them estimated. Never presented as machine timing.
- **Editing gated:** all edit operations require a finalized/recovered session (`session.EndedAtUtc != null`) — enforced in `EditStore.EnsureFinalizedAsync`.
- **Byte-identity:** `transcript.md`/`.txt`/`session.txt`/`.docx` output for **un-split, un-changed** sessions must stay byte-identical. Existing renderer tests are the guard; every projection change must keep them green.
- **All disk mutation from the UI goes through `MaintenanceService`** under its per-session single-flight gate (`RunForSessionAsync`). ViewModels never call `SessionWriter`/`EditStore` directly.
- **WPF-free Core:** everything under `src/LocalScribe.Core` has no WPF references.
- **No Unicode emojis in test scripts** (user rule).
- **Zero-warning build gate:** `dotnet build` must stay 0-warning; the App test suite and Core suite (366 + 2 known fixture fails) stay green.

**Build/test commands** (run from `F:\LocalScribe`; close a running `LocalScribe.App.exe` first — it locks `Core.dll`/the app exe and causes MSB3027 copy errors that are NOT compile failures):
- Core tests: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`
- App tests: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`
- Single test: append ` --filter "FullyQualifiedName~TestClassName.TestMethod"`

---

## Phase 1 — Core: split overlay model + store

### Task 1: `SplitPart` record + `Edits.Splits` field

**Files:**
- Modify: `src/LocalScribe.Core/Model/Edits.cs`
- Test: `tests/LocalScribe.Core.Tests/EditsModelTests.cs` (create)

**Interfaces:**
- Produces: `SplitPart { string Text; long StartMs; bool DerivedStart; string? SpeakerParticipantId; string? SpeakerClusterKey }`, `SplitEntry { TranscriptSource Source; DateTimeOffset EditedAtUtc; IReadOnlyList<SplitPart> Parts }`, and `Edits.Splits : IReadOnlyDictionary<string, SplitEntry>` (keyed by seq string, default empty).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/EditsModelTests.cs
using System.Text.Json;
using LocalScribe.Core.Model;
using Xunit;

public class EditsModelTests
{
    [Fact]
    public void Splits_RoundTripsThroughJson()
    {
        var edits = new Edits
        {
            Splits = new Dictionary<string, SplitEntry>
            {
                ["7"] = new SplitEntry
                {
                    Source = TranscriptSource.Remote,
                    EditedAtUtc = DateTimeOffset.Parse("2026-07-07T10:00:00Z"),
                    Parts =
                    [
                        new SplitPart { Text = "First half.", StartMs = 15000, DerivedStart = false },
                        new SplitPart { Text = "Second half.", StartMs = 16470, DerivedStart = true,
                                        SpeakerParticipantId = "p-2" },
                    ],
                },
            },
        };

        string json = JsonSerializer.Serialize(edits, LocalScribeJson.Options);
        var back = JsonSerializer.Deserialize<Edits>(json, LocalScribeJson.Options)!;

        Assert.Single(back.Splits);
        var entry = back.Splits["7"];
        Assert.Equal(TranscriptSource.Remote, entry.Source);
        Assert.Equal(2, entry.Parts.Count);
        Assert.Equal("Second half.", entry.Parts[1].Text);
        Assert.True(entry.Parts[1].DerivedStart);
        Assert.Equal("p-2", entry.Parts[1].SpeakerParticipantId);
    }

    [Fact]
    public void Splits_DefaultsToEmpty()
        => Assert.Empty(new Edits().Splits);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~EditsModelTests"`
Expected: FAIL — `SplitEntry`/`SplitPart` do not exist; `Edits` has no `Splits`.

- [ ] **Step 3: Write minimal implementation**

```csharp
// Append to src/LocalScribe.Core/Model/Edits.cs (inside namespace LocalScribe.Core.Model)

/// <summary>One human-authored child of a split segment (design §2.1). Text is the child's
/// displayed content; StartMs is its start (full ms). DerivedStart is false only for the first
/// part, which inherits the machine segment's start; later parts carry a human estimate. Speaker
/// is an OPTIONAL override resolved at projection: at most one of SpeakerParticipantId /
/// SpeakerClusterKey is set; null on both means the child inherits the parent seq's resolved
/// name. Stored in the split entry, NOT speakers.json, so speakers.json stays integer-seq keyed.</summary>
public sealed record SplitPart
{
    public string Text { get; init; } = "";
    public long StartMs { get; init; }
    public bool DerivedStart { get; init; }
    public string? SpeakerParticipantId { get; init; }
    public string? SpeakerClusterKey { get; init; }
}

/// <summary>A non-destructive split overlay for one machine segment (design §2). Partitions the
/// original into Parts (>= 2, display order); the machine transcript.jsonl line is untouched and
/// revert = removing this entry.</summary>
public sealed record SplitEntry
{
    public TranscriptSource Source { get; init; }
    public DateTimeOffset EditedAtUtc { get; init; }
    public IReadOnlyList<SplitPart> Parts { get; init; } = [];
}
```

Add the field to the existing `Edits` record (place after `Corrections`):

```csharp
    public IReadOnlyDictionary<string, SplitEntry> Splits { get; init; } = new Dictionary<string, SplitEntry>();
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~EditsModelTests"`
Expected: PASS (both facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/Edits.cs tests/LocalScribe.Core.Tests/EditsModelTests.cs
git commit -m "feat(core): add splits overlay model to edits.json"
```

---

### Task 2: `EditStore.ApplySplitAsync` — write + validate a split

**Files:**
- Modify: `src/LocalScribe.Core/Storage/EditStore.cs`
- Test: `tests/LocalScribe.Core.Tests/EditStoreSplitTests.cs` (create)

**Interfaces:**
- Consumes: `SplitPart`, `SplitEntry`, `Edits.Splits` (Task 1); existing `EnsureFinalizedAsync`, `EnsureSegmentAsync`, `LoadAsync`, `MarkEditedAsync`, `EditsPath`.
- Produces: `Task ApplySplitAsync(int seq, TranscriptSource source, IReadOnlyList<SplitPart> parts, CancellationToken ct)`. Validates and writes `splits[seq]`, and **removes** any `corrections[seq]` (its text is absorbed into the parts). Flips `meta.Edited`.

Validation rules (throw `ArgumentException` on violation): `parts.Count >= 2`; every `part.Text` non-null/non-whitespace; `parts[0].StartMs == originalLine.StartMs` and `parts[0].DerivedStart == false`; `parts[i].StartMs` strictly increasing; every `parts[i].StartMs` in `(originalLine.StartMs, originalLine.EndMs]` for `i >= 1`; `parts[i>=1].DerivedStart == true`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/EditStoreSplitTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

public class EditStoreSplitTests
{
    // Mirrors the harness EditStoreTests uses: a temp session dir with a finalized session.json,
    // a meta.json, and a transcript.jsonl. Reuse the existing test helper if EditStoreTests has one;
    // otherwise this local builder is self-contained.
    private static async Task<string> NewFinalizedSessionAsync(params TranscriptLine[] lines)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.Parse("2026-07-06T07:45:00Z");
        await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(
            new SessionRecord { Id = "s", App = AppKind.Manual, StartedAtUtc = now,
                EndedAtUtc = now.AddSeconds(33), DurationMs = 33000,
                Sources = [TranscriptSource.Local, TranscriptSource.Remote] }, CancellationToken.None);
        await new MetadataStore(Path.Combine(dir, "meta.json")).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Manual, now, self: null), CancellationToken.None);
        var store = new TranscriptStore(Path.Combine(dir, "transcript.jsonl"));
        foreach (var l in lines) await store.AppendAsync(l, CancellationToken.None);
        return dir;
    }

    [Fact]
    public async Task ApplySplit_WritesEntry_AndClearsPriorCorrection()
    {
        string dir = await NewFinalizedSessionAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Two speakers here."));
        var store = new EditStore(dir, TimeProvider.System);
        await store.ApplyTextCorrectionAsync(3, "Two speakers here (fixed).", CancellationToken.None);

        await store.ApplySplitAsync(3, TranscriptSource.Remote,
        [
            new SplitPart { Text = "Two speakers", StartMs = 15000, DerivedStart = false },
            new SplitPart { Text = "here.", StartMs = 16000, DerivedStart = true },
        ], CancellationToken.None);

        var edits = await store.LoadAsync(CancellationToken.None);
        Assert.True(edits!.Splits.ContainsKey("3"));
        Assert.False(edits.Corrections.ContainsKey("3"));   // absorbed
        Assert.Equal(2, edits.Splits["3"].Parts.Count);
    }

    [Fact]
    public async Task ApplySplit_RejectsWhitespaceChild()
    {
        string dir = await NewFinalizedSessionAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there."));
        var store = new EditStore(dir, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplySplitAsync(3, TranscriptSource.Remote,
        [
            new SplitPart { Text = "Hello", StartMs = 15000, DerivedStart = false },
            new SplitPart { Text = "   ", StartMs = 16000, DerivedStart = true },
        ], CancellationToken.None));
    }

    [Fact]
    public async Task ApplySplit_RejectsFirstStartNotMachineStart()
    {
        string dir = await NewFinalizedSessionAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there."));
        var store = new EditStore(dir, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplySplitAsync(3, TranscriptSource.Remote,
        [
            new SplitPart { Text = "Hello", StartMs = 15500, DerivedStart = false },   // != 15000
            new SplitPart { Text = "there.", StartMs = 16000, DerivedStart = true },
        ], CancellationToken.None));
    }

    [Fact]
    public async Task ApplySplit_RejectsOutOfRangeOrNonMonotonicStart()
    {
        string dir = await NewFinalizedSessionAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there."));
        var store = new EditStore(dir, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplySplitAsync(3, TranscriptSource.Remote,
        [
            new SplitPart { Text = "Hello", StartMs = 15000, DerivedStart = false },
            new SplitPart { Text = "there.", StartMs = 99000, DerivedStart = true },   // > endMs 17000
        ], CancellationToken.None));
    }
}
```

> If `EditStoreTests.cs` already exposes a session-dir builder helper, call it instead of `NewFinalizedSessionAsync` and delete the local copy. Check `tests/LocalScribe.Core.Tests/EditStoreTests.cs` first; match its exact `SessionRecord`/`TranscriptLine.Segment` construction (the field names above are from `Model/`—verify against the real records and adjust if a required field is missing).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~EditStoreSplitTests"`
Expected: FAIL — `ApplySplitAsync` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// Add to src/LocalScribe.Core/Storage/EditStore.cs

/// <summary>Write a non-destructive split overlay for one segment (design §2). The machine
/// transcript.jsonl line is untouched; the split partitions it into >= 2 human-authored parts.
/// Any prior text correction on the seq is REMOVED (absorbed into the parts) so display text has
/// one source of truth. Validators enforce the evidentiary invariants (non-blank children;
/// first part inherits the machine start; later starts strictly increasing and within the
/// segment). Flips meta.Edited.</summary>
public async Task ApplySplitAsync(int seq, TranscriptSource source, IReadOnlyList<SplitPart> parts,
    CancellationToken ct)
{
    if (parts.Count < 2)
        throw new ArgumentException("a split needs at least two parts.", nameof(parts));
    await EnsureFinalizedAsync(ct);
    await EnsureSegmentAsync(seq, expectedSource: source, ct);

    var lines = await new TranscriptStore(JsonlPath).ReadAllAsync(ct);
    var line = lines.First(l => l.Seq == seq);   // EnsureSegmentAsync already proved it exists

    for (int i = 0; i < parts.Count; i++)
    {
        if (string.IsNullOrWhiteSpace(parts[i].Text))
            throw new ArgumentException(
                $"split part {i} of seq {seq} is empty; transcript content is never removed (spec §1.6).",
                nameof(parts));
    }
    if (parts[0].StartMs != line.StartMs || parts[0].DerivedStart)
        throw new ArgumentException(
            $"first split part of seq {seq} must inherit the machine start {line.StartMs} (not derived).",
            nameof(parts));
    for (int i = 1; i < parts.Count; i++)
    {
        if (!parts[i].DerivedStart)
            throw new ArgumentException($"split part {i} of seq {seq} must be flagged DerivedStart.", nameof(parts));
        if (parts[i].StartMs <= parts[i - 1].StartMs)
            throw new ArgumentException($"split part starts for seq {seq} must strictly increase.", nameof(parts));
        if (parts[i].StartMs <= line.StartMs || parts[i].StartMs > line.EndMs)
            throw new ArgumentException(
                $"split part {i} start {parts[i].StartMs} for seq {seq} is outside ({line.StartMs}, {line.EndMs}].",
                nameof(parts));
    }

    var edits = await LoadAsync(ct) ?? new Edits();
    var splits = new Dictionary<string, SplitEntry>(edits.Splits)
    {
        [seq.ToString()] = new SplitEntry { Source = source, EditedAtUtc = _time.GetUtcNow(), Parts = parts },
    };
    var corrections = new Dictionary<string, Correction>(edits.Corrections);
    corrections.Remove(seq.ToString());   // absorbed into parts

    await JsonFile.WriteAsync(EditsPath,
        edits with { SchemaVersion = Version, Corrections = corrections, Splits = splits }, ct);
    await MarkEditedAsync(ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~EditStoreSplitTests"`
Expected: PASS (all four facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/EditStore.cs tests/LocalScribe.Core.Tests/EditStoreSplitTests.cs
git commit -m "feat(core): EditStore.ApplySplitAsync with evidentiary validators"
```

---

### Task 3: `EditStore.RemoveSplitAsync` — revert a split

**Files:**
- Modify: `src/LocalScribe.Core/Storage/EditStore.cs`
- Test: `tests/LocalScribe.Core.Tests/EditStoreSplitTests.cs` (add)

**Interfaces:**
- Produces: `Task<bool> RemoveSplitAsync(int seq, CancellationToken ct)` — removes `splits[seq]`, flips `meta.Edited`, returns `false` (writes nothing) when there was no split for that seq.

- [ ] **Step 1: Write the failing test**

```csharp
// add to EditStoreSplitTests
[Fact]
public async Task RemoveSplit_RestoresSingleSegment_AndIsNoOpWhenAbsent()
{
    string dir = await NewFinalizedSessionAsync(
        TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there."));
    var store = new EditStore(dir, TimeProvider.System);
    await store.ApplySplitAsync(3, TranscriptSource.Remote,
    [
        new SplitPart { Text = "Hello", StartMs = 15000, DerivedStart = false },
        new SplitPart { Text = "there.", StartMs = 16000, DerivedStart = true },
    ], CancellationToken.None);

    Assert.True(await store.RemoveSplitAsync(3, CancellationToken.None));
    var edits = await store.LoadAsync(CancellationToken.None);
    Assert.Empty(edits!.Splits);
    Assert.False(await store.RemoveSplitAsync(3, CancellationToken.None));   // second time: no-op
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~RemoveSplit_RestoresSingleSegment"`
Expected: FAIL — `RemoveSplitAsync` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// Add to src/LocalScribe.Core/Storage/EditStore.cs

/// <summary>Revert a split (design §2): remove splits[seq], restoring the single machine
/// segment. Quiet no-op (false, writes nothing) when the seq was not split. Does NOT resurrect a
/// prior correction — the machine floor is the revert target.</summary>
public async Task<bool> RemoveSplitAsync(int seq, CancellationToken ct)
{
    await EnsureFinalizedAsync(ct);
    var edits = await LoadAsync(ct);
    if (edits is null || !edits.Splits.ContainsKey(seq.ToString())) return false;
    var splits = new Dictionary<string, SplitEntry>(edits.Splits);
    splits.Remove(seq.ToString());
    await JsonFile.WriteAsync(EditsPath, edits with { SchemaVersion = Version, Splits = splits }, ct);
    await MarkEditedAsync(ct);
    return true;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~EditStoreSplitTests"`
Expected: PASS (all five facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/EditStore.cs tests/LocalScribe.Core.Tests/EditStoreSplitTests.cs
git commit -m "feat(core): EditStore.RemoveSplitAsync (revert split)"
```

---

## Phase 2 — Core: projection expansion

### Task 4: `ProjectedSegment` split-child fields

**Files:**
- Modify: `src/LocalScribe.Core/Projection/ProjectedSegment.cs`
- Test: `tests/LocalScribe.Core.Tests/ProjectedSegmentTests.cs` (create)

**Interfaces:**
- Produces: additive optional params on `ProjectedSegment`: `bool IsSplitChild = false`, `int PartIndex = 0`, `long? StartMsOverride = null`, `long? EndMsOverride = null`, `string? SpeakerParticipantId = null`, `string? SpeakerClusterKey = null`. `StartMs`/`EndMs` return the override when set, else the line's. Existing 3-arg construction is unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/ProjectedSegmentTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class ProjectedSegmentTests
{
    private static TranscriptLine Seg() =>
        TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 4000, "hi");

    [Fact]
    public void Defaults_MatchLine_AndAreNotSplitChildren()
    {
        var p = new ProjectedSegment(Seg(), "hi");
        Assert.False(p.IsSplitChild);
        Assert.Equal(1000, p.StartMs);
        Assert.Equal(4000, p.EndMs);
    }

    [Fact]
    public void Overrides_WinForSplitChild()
    {
        var p = new ProjectedSegment(Seg(), "half", IsSplitChild: true, PartIndex: 1,
            StartMsOverride: 2500, EndMsOverride: 4000, SpeakerParticipantId: "p-2");
        Assert.True(p.IsSplitChild);
        Assert.Equal(2500, p.StartMs);
        Assert.Equal("p-2", p.SpeakerParticipantId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~ProjectedSegmentTests"`
Expected: FAIL — extra params/props don't exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Projection/ProjectedSegment.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Projection;

/// <summary>A segment paired with its projected text (post vocabulary + edits), carrying the
/// original line so name resolution still works. For a split child (design §2.2) the text, times,
/// and speaker come from the split part while Line stays the untouched machine original — so
/// RawText and seq/source still resolve from Line.</summary>
public sealed record ProjectedSegment(TranscriptLine Line, string Text, bool Corrected = false,
    bool IsSplitChild = false, int PartIndex = 0,
    long? StartMsOverride = null, long? EndMsOverride = null,
    string? SpeakerParticipantId = null, string? SpeakerClusterKey = null)
{
    public int Seq => Line.Seq;
    public TranscriptSource Source => Line.Source;
    public long StartMs => StartMsOverride ?? Line.StartMs;
    public long EndMs => EndMsOverride ?? Line.EndMs;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~ProjectedSegmentTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/ProjectedSegment.cs tests/LocalScribe.Core.Tests/ProjectedSegmentTests.cs
git commit -m "feat(core): ProjectedSegment split-child fields (additive)"
```

---

### Task 5: `RowSegment.IsSplitChild`/`PartIndex` + `DisplayRow.HasSplit`

**Files:**
- Modify: `src/LocalScribe.Core/Projection/RowSegment.cs`, `src/LocalScribe.Core/Projection/DisplayRow.cs`
- Test: `tests/LocalScribe.Core.Tests/DisplayRowTests.cs` (create)

**Interfaces:**
- Produces: `RowSegment` gains trailing `bool IsSplitChild = false, int PartIndex = 0`; `DisplayRow.HasSplit => Segments.Any(s => s.IsSplitChild)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/DisplayRowTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class DisplayRowTests
{
    [Fact]
    public void HasSplit_TrueWhenAnyChildIsSplit()
    {
        var row = new DisplayRow
        {
            DisplayName = "Them", Text = "a b",
            Segments =
            [
                new RowSegment(3, TranscriptSource.Remote, 15000, 16000, "a", "a b", false, false,
                    IsSplitChild: true, PartIndex: 0),
                new RowSegment(3, TranscriptSource.Remote, 16000, 17000, "b", "a b", false, false,
                    IsSplitChild: true, PartIndex: 1),
            ],
        };
        Assert.True(row.HasSplit);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~DisplayRowTests"`
Expected: FAIL — `RowSegment` has no `IsSplitChild`; `DisplayRow` has no `HasSplit`.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Projection/RowSegment.cs — extend the positional record
public sealed record RowSegment(
    int Seq, TranscriptSource Source, long StartMs, long EndMs,
    string ProjectedText, string RawText, bool IsCorrected, bool IsPinned,
    bool IsSplitChild = false, int PartIndex = 0);
```

```csharp
// src/LocalScribe.Core/Projection/DisplayRow.cs — add next to HasCorrection/HasPin
    public bool HasSplit => Segments.Any(s => s.IsSplitChild);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~DisplayRowTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/RowSegment.cs src/LocalScribe.Core/Projection/DisplayRow.cs tests/LocalScribe.Core.Tests/DisplayRowTests.cs
git commit -m "feat(core): RowSegment split-child identity + DisplayRow.HasSplit"
```

---

### Task 6: `NameResolver` clusterKey / participantId override

**Files:**
- Modify: `src/LocalScribe.Core/Projection/NameResolver.cs`
- Test: `tests/LocalScribe.Core.Tests/NameResolverOverrideTests.cs` (create)

**Interfaces:**
- Produces: overload `NameResolver.Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta, string? participantIdOverride, string? clusterKeyOverride)`. A non-null `participantIdOverride` returns that participant's `Name` (empty → falls through to normal resolution); a non-null `clusterKeyOverride` resolves via the existing tier-1a/1b logic; both null → the existing `Resolve` behavior. Existing 3-arg `Resolve` is preserved (delegates with both overrides null).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/NameResolverOverrideTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class NameResolverOverrideTests
{
    private static TranscriptLine Seg() =>
        TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "hi");

    [Fact]
    public void ParticipantOverride_ReturnsParticipantName()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null) with
        {
            Participants =
            [
                new SessionParticipant { Id = "p-2", Name = "Ms. Adams", Side = SourceKind.Remote,
                    Kind = ParticipantKind.Named },
            ],
        };
        Assert.Equal("Ms. Adams", NameResolver.Resolve(Seg(), speakers: null, meta,
            participantIdOverride: "p-2", clusterKeyOverride: null));
    }

    [Fact]
    public void ClusterOverride_ResolvesViaNamesOverlay()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null);
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:4"] = "Detected Voice B" },
        };
        Assert.Equal("Detected Voice B", NameResolver.Resolve(Seg(), speakers, meta,
            participantIdOverride: null, clusterKeyOverride: "Remote:4"));
    }

    [Fact]
    public void NoOverride_MatchesLegacyResolve()
    {
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null);
        Assert.Equal(NameResolver.Resolve(Seg(), null, meta),
            NameResolver.Resolve(Seg(), null, meta, null, null));
    }
}
```

> Verify `SessionParticipant` / `SessionMeta` / `Speakers` construction against the real records (Task 6 references `Model/SessionParticipant.cs`, `Model/Speakers.cs`); adjust field names if the init-only shapes differ.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~NameResolverOverrideTests"`
Expected: FAIL — 5-arg overload missing.

- [ ] **Step 3: Write minimal implementation**

Refactor the existing tier-1 cluster resolution into a reusable helper and add the overload:

```csharp
// src/LocalScribe.Core/Projection/NameResolver.cs — replace the class body

public static class NameResolver
{
    public static string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta)
        => Resolve(segment, speakers, meta, participantIdOverride: null, clusterKeyOverride: null);

    public static string Resolve(TranscriptLine segment, Speakers? speakers, SessionMeta meta,
        string? participantIdOverride, string? clusterKeyOverride)
    {
        // Split-child speaker overrides (design §2.4) win first.
        if (!string.IsNullOrEmpty(participantIdOverride))
        {
            var p = meta.Participants.FirstOrDefault(x => x.Id == participantIdOverride);
            if (p is not null && !string.IsNullOrEmpty(p.Name)) return p.Name;
        }
        if (!string.IsNullOrEmpty(clusterKeyOverride))
            return ResolveClusterKey(clusterKeyOverride, speakers, meta);

        string sourceKey = segment.Source.ToString();
        SourceKind side = segment.Source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;

        if (speakers is not null
            && speakers.Assignments.TryGetValue(sourceKey, out var bySeq)
            && bySeq.TryGetValue(segment.Seq.ToString(), out string? clusterKey))
            return ResolveClusterKey(clusterKey, speakers, meta);

        int declared = side == SourceKind.Local ? meta.LocalCount : meta.RemoteCount;
        if (declared == 1)
        {
            SessionParticipant? lone = null;
            int namedOnSide = 0;
            foreach (var p in meta.Participants)
                if (p.Side == side && p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name))
                { namedOnSide++; lone = p; }
            if (namedOnSide == 1) return lone!.Name;
        }

        if (!string.IsNullOrEmpty(segment.SpeakerLabel)) return segment.SpeakerLabel;
        return side == SourceKind.Local ? "Me" : "Them";
    }

    // Tier 1a/1b extracted verbatim so a split-child override resolves a clusterKey the same way.
    private static string ResolveClusterKey(string clusterKey, Speakers? speakers, SessionMeta meta)
    {
        SessionParticipant? owner = meta.Participants.FirstOrDefault(p =>
            p.ClusterKey == clusterKey && p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name));
        if (owner is not null) return owner.Name;
        if (speakers is not null && speakers.Names.TryGetValue(clusterKey, out string? named)) return named;
        int colon = clusterKey.IndexOf(':');
        string clusterId = colon >= 0 ? clusterKey[(colon + 1)..] : clusterKey;
        return "Speaker " + clusterId;
    }
}
```

Keep the required `using LocalScribe.Core.Audio;` / `Model;` headers at the top of the file.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~NameResolver"`
Expected: PASS — new override tests and the existing `NameResolver` tests both green (behavior-preserving refactor).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/NameResolver.cs tests/LocalScribe.Core.Tests/NameResolverOverrideTests.cs
git commit -m "feat(core): NameResolver split-child speaker override"
```

---

### Task 7: `PhantomBleedDedup` exempts split children

**Files:**
- Modify: `src/LocalScribe.Core/Projection/PhantomBleedDedup.cs`
- Test: `tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs` (add — or create if absent)

**Interfaces:**
- Consumes: `ProjectedSegment.IsSplitChild` (Task 4).
- Produces: the Local-keep guard becomes `!(s.Corrected || s.IsSplitChild)` — a human split is an explicit keep, same as a correction.

- [ ] **Step 1: Write the failing test**

```csharp
// add to tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs
[Fact]
public void SplitChild_IsNeverSuppressed()
{
    var remote = new ProjectedSegment(
        TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "identical words here"), "identical words here");
    var localChild = new ProjectedSegment(
        TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "identical words here"),
        "identical words here", IsSplitChild: true, PartIndex: 0);

    var kept = new PhantomBleedDedup().Filter([remote, localChild]);
    Assert.Contains(localChild, kept);   // exempt despite matching the remote
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SplitChild_IsNeverSuppressed"`
Expected: FAIL — the local child is suppressed (currently only `Corrected` is exempt).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Projection/PhantomBleedDedup.cs — in Filter's loop, change the guard:
if (s.Source == TranscriptSource.Local && !(s.Corrected || s.IsSplitChild) && remotes.Any(r => IsBleedOf(s, r)))
    continue;
```

Update the class doc comment: "A human-corrected OR human-split segment (design §2.2) is always exempt."

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~PhantomBleedDedup"`
Expected: PASS (new test + existing dedup tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/PhantomBleedDedup.cs tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs
git commit -m "feat(core): exempt split children from phantom-bleed dedup"
```

---

### Task 8: `TranscriptProjection.Build` expands splits into N children

**Files:**
- Modify: `src/LocalScribe.Core/Projection/TranscriptProjection.cs`
- Test: `tests/LocalScribe.Core.Tests/TranscriptProjectionSplitTests.cs` (create)

**Interfaces:**
- Consumes: `Edits.Splits`, `SplitPart` (Task 1); `ProjectedSegment` overrides (Task 4); `RowSegment` fields (Task 5); `NameResolver` override (Task 6).
- Produces: in step (1)-(3), a segment whose `seq` is in `edits.Splits` emits one `ProjectedSegment` per part (text/time/partIndex/speaker overrides, `IsSplitChild: true`) instead of one; `EndMs` of part `i` is `parts[i+1].StartMs` (or the line's `EndMs` for the last part). Step (5) forwards the child's speaker overrides to `NameResolver.Resolve` and stamps `RowSegment.IsSplitChild`/`PartIndex`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/TranscriptProjectionSplitTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Vocabulary;
using Xunit;

public class TranscriptProjectionSplitTests
{
    private sealed class NoVocab : IVocabularyProvider
    {
        public string BuildInitialPrompt(IReadOnlyList<string> matterIds) => "";
        public string ApplyCorrections(string text, IReadOnlyList<string> matterIds) => text;
    }

    private static TranscriptProjection New() => new(new NoVocab(), new PhantomBleedDedup());

    [Fact]
    public void SplitSeq_ExpandsIntoChildRows_WithDerivedTimesAndSpeaker()
    {
        var lines = new List<TranscriptLine>
        {
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "First. Second."),
        };
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null) with
        {
            RemoteCount = 2,
            Participants =
            [
                new SessionParticipant { Id = "p-2", Name = "Ms. Adams", Side = SourceKind.Remote,
                    Kind = ParticipantKind.Named },
            ],
        };
        var edits = new Edits
        {
            Splits = new Dictionary<string, SplitEntry>
            {
                ["3"] = new SplitEntry
                {
                    Source = TranscriptSource.Remote,
                    Parts =
                    [
                        new SplitPart { Text = "First.", StartMs = 15000, DerivedStart = false },
                        new SplitPart { Text = "Second.", StartMs = 16000, DerivedStart = true,
                                        SpeakerParticipantId = "p-2" },
                    ],
                },
            },
        };

        var rows = New().Build(lines, speakers: null, edits, meta);

        // Two children; the second is a new section because its speaker differs (Ms. Adams).
        var allSegments = rows.SelectMany(r => r.Segments).ToList();
        Assert.Equal(2, allSegments.Count);
        Assert.All(allSegments, s => Assert.True(s.IsSplitChild));
        Assert.Equal(new[] { 0, 1 }, allSegments.Select(s => s.PartIndex));
        Assert.Equal(16000, allSegments[1].StartMs);
        Assert.Equal(17000, allSegments[1].EndMs);          // inherits the line end
        Assert.Contains(rows, r => r.DisplayName == "Ms. Adams");
    }

    [Fact]
    public void UnsplitSession_ProducesOneSegmentPerLine()
    {
        var lines = new List<TranscriptLine>
        {
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 2000, "hello"),
        };
        var meta = SessionMeta.CreateDefault(AppKind.Manual, DateTimeOffset.UtcNow, self: null);
        var rows = New().Build(lines, null, new Edits(), meta);
        Assert.Single(rows.SelectMany(r => r.Segments));
        Assert.False(rows[0].HasSplit);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~TranscriptProjectionSplitTests"`
Expected: FAIL — splits are ignored; only one row/segment is produced.

- [ ] **Step 3: Write minimal implementation**

Replace the step (1)-(3) loop and the step (5) `PreRow` construction in `TranscriptProjection.Build`:

```csharp
// step (1)-(3): partition; vocabulary; edits overlay; SPLIT expansion.
var projected = new List<ProjectedSegment>();
var markers = new List<TranscriptLine>();
foreach (var line in lines)
{
    if (line.Kind == TranscriptKind.Marker) { markers.Add(line); continue; }
    string text = _vocab.ApplyCorrections(line.Text, matterIds);

    if (edits is not null && edits.Splits.TryGetValue(line.Seq.ToString(), out var split))
    {
        for (int i = 0; i < split.Parts.Count; i++)
        {
            var part = split.Parts[i];
            long endMs = i + 1 < split.Parts.Count ? split.Parts[i + 1].StartMs : line.EndMs;
            projected.Add(new ProjectedSegment(line, part.Text, Corrected: false,
                IsSplitChild: true, PartIndex: i,
                StartMsOverride: part.StartMs, EndMsOverride: endMs,
                SpeakerParticipantId: part.SpeakerParticipantId,
                SpeakerClusterKey: part.SpeakerClusterKey));
        }
        continue;
    }

    Correction? c = null;
    bool corrected = edits is not null && edits.Corrections.TryGetValue(line.Seq.ToString(), out c);
    if (corrected) text = c!.Text;
    projected.Add(new ProjectedSegment(line, text, corrected));
}
```

```csharp
// step (5): PreRow construction — forward split-child speaker + identity.
foreach (var s in kept)
{
    bool pinned = pinnedBySource.TryGetValue(s.Source.ToString(), out var pins)
        && pins.Contains(s.Seq.ToString());
    string name = NameResolver.Resolve(s.Line, speakers, meta,
        s.SpeakerParticipantId, s.SpeakerClusterKey);
    pre.Add(new PreRow(s.StartMs, s.EndMs, Rank(s.Source), s.Seq, name, s.Text, IsMarker: false,
        Segment: new RowSegment(s.Seq, s.Source, s.StartMs, s.EndMs,
            ProjectedText: s.Text, RawText: s.Line.Text, s.Corrected, pinned,
            IsSplitChild: s.IsSplitChild, PartIndex: s.PartIndex)));
}
```

> Note: `PreRow.Sort` uses `(StartMs, SourceRank, Seq)`; split children share a `seq`, so keep them in emit order — the sort is stable and their `StartMs` already increases, so order is preserved.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~TranscriptProjection"`
Expected: PASS — split tests plus the existing projection/renderer byte-identity tests stay green.

- [ ] **Step 5: Run the FULL Core suite to prove byte-identity held**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`
Expected: PASS (366 + 2 known fixture fails; no NEW failures). If any renderer/exporter test regressed, an un-split session's projection changed — stop and fix.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Projection/TranscriptProjection.cs tests/LocalScribe.Core.Tests/TranscriptProjectionSplitTests.cs
git commit -m "feat(core): expand split overlay into child rows in projection"
```

---

## Phase 3 — App: the editor save path

### Task 9: `MaintenanceService.SaveTranscriptEditsAsync`

**Files:**
- Modify: `src/LocalScribe.App/Services/MaintenanceService.cs`
- Create: `src/LocalScribe.App/Services/TranscriptEditBatch.cs`
- Test: `tests/LocalScribe.App.Tests/MaintenanceServiceEditorTests.cs` (create)

**Interfaces:**
- Produces DTOs (in `TranscriptEditBatch.cs`):
  ```csharp
  public sealed record SplitPartEdit(string Text, long StartMs, bool DerivedStart,
      string? SpeakerParticipantId, string? SpeakerClusterKey);
  public sealed record SplitEdit(int Seq, TranscriptSource Source, IReadOnlyList<SplitPartEdit> Parts);
  public sealed record TranscriptEditBatch(
      IReadOnlyDictionary<int, string> Corrections,
      IReadOnlyCollection<int> CorrectionReverts,
      IReadOnlyList<SplitEdit> Splits,
      IReadOnlyCollection<int> SplitReverts);
  ```
- Produces method: `Task<bool> SaveTranscriptEditsAsync(string sessionId, TranscriptEditBatch batch, CancellationToken ct)` — under the per-session gate: delete-race guard, apply corrections (`ApplyTextEditsAsync`), apply/revert splits (`ApplySplitAsync`/`RemoveSplitAsync`), then ONE `RegenerateProjectionsAsync` if anything changed. Whole-section speaker pins are NOT part of this batch — the editor VM calls the existing `SaveSpeakerPinsAsync` for those (Task 12). Returns `false` when deleted mid-save or a pure no-op.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/MaintenanceServiceEditorTests.cs
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

public class MaintenanceServiceEditorTests
{
    // Follow the existing MaintenanceService test harness (see MaintenanceServiceDiarisationTests
    // for the StoragePaths + settings + FakeRecycleBin + TimeProvider setup) to build `svc` and a
    // finalized session `sid` with one Remote segment seq 3 (15000..17000, "First. Second.").

    [Fact]
    public async Task SaveTranscriptEdits_PersistsSplit_AndRegensProjection()
    {
        var (svc, paths, sid) = await EditorHarness.NewSessionWithRemoteSegmentAsync();
        var batch = new TranscriptEditBatch(
            Corrections: new Dictionary<int, string>(),
            CorrectionReverts: [],
            Splits:
            [
                new SplitEdit(3, TranscriptSource.Remote,
                [
                    new SplitPartEdit("First.", 15000, false, null, null),
                    new SplitPartEdit("Second.", 16000, true, null, null),
                ]),
            ],
            SplitReverts: []);

        bool changed = await svc.SaveTranscriptEditsAsync(sid, batch, CancellationToken.None);

        Assert.True(changed);
        var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System)
            .LoadAsync(CancellationToken.None);
        Assert.True(edits!.Splits.ContainsKey("3"));
        // regen ran: transcript.md now shows both halves as separate turns is asserted in the
        // read-view VM test (Task 10); here assert the overlay + that meta.Edited flipped.
        var meta = await new MetadataStore(paths.MetaJson(sid)).LoadAsync(CancellationToken.None);
        Assert.True(meta!.Edited);
    }

    [Fact]
    public async Task SaveTranscriptEdits_NoOpBatch_ReturnsFalse()
    {
        var (svc, _, sid) = await EditorHarness.NewSessionWithRemoteSegmentAsync();
        bool changed = await svc.SaveTranscriptEditsAsync(sid,
            new TranscriptEditBatch(new Dictionary<int, string>(), [], [], []), CancellationToken.None);
        Assert.False(changed);
    }
}
```

> Create `EditorHarness.NewSessionWithRemoteSegmentAsync()` in the test project by copying the setup already used in `MaintenanceServiceDiarisationTests` (temp `StoragePaths`, `FakeSettingsService`, a fake `IRecycleBin`, `TimeProvider.System`), writing a finalized `session.json` + `meta.json` + a one-line `transcript.jsonl`. Return `(MaintenanceService svc, StoragePaths paths, string sid)`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MaintenanceServiceEditorTests"`
Expected: FAIL — `TranscriptEditBatch`/`SaveTranscriptEditsAsync` not defined.

- [ ] **Step 3: Write minimal implementation**

Create `src/LocalScribe.App/Services/TranscriptEditBatch.cs` with the DTOs above (namespace `LocalScribe.App.Services`, `using LocalScribe.Core.Model;`).

Add to `MaintenanceService`:

```csharp
/// <summary>The one write path for an Edit-mode save (design §3.4): apply text corrections and
/// split overlays (and their reverts) to edits.json under the per-session gate, then ONE
/// projection regen. Whole-section speaker pins go through SaveSpeakerPinsAsync separately (the
/// editor VM calls it), keeping this method's writes confined to edits.json. Returns false when
/// the session was deleted mid-save or the whole batch was a no-op.</summary>
public Task<bool> SaveTranscriptEditsAsync(string sessionId, TranscriptEditBatch batch, CancellationToken ct)
    => RunForSessionAsync(sessionId, async inner =>
    {
        if (!File.Exists(paths.SessionJson(sessionId))) return false;
        var store = new EditStore(paths.SessionDir(sessionId), time);
        bool changed = false;

        // Corrections first (splits clear a seq's correction, so ordering is safe either way).
        if (batch.Corrections.Count > 0 || batch.CorrectionReverts.Count > 0)
            changed |= await store.ApplyTextEditsAsync(batch.Corrections, batch.CorrectionReverts, inner);

        foreach (int seq in batch.SplitReverts)
            changed |= await store.RemoveSplitAsync(seq, inner);

        foreach (var s in batch.Splits)
        {
            var parts = s.Parts.Select(p => new SplitPart
            {
                Text = p.Text, StartMs = p.StartMs, DerivedStart = p.DerivedStart,
                SpeakerParticipantId = p.SpeakerParticipantId, SpeakerClusterKey = p.SpeakerClusterKey,
            }).ToList();
            await store.ApplySplitAsync(s.Seq, s.Source, parts, inner);
            changed = true;
        }

        if (changed)
            await new SessionWriter(paths, settings.Current, time).RegenerateProjectionsAsync(sessionId, inner);
        return changed;
    }, ct);
```

Add `using LocalScribe.Core.Model;` if not already imported (it is).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MaintenanceServiceEditorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/Services/TranscriptEditBatch.cs src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceEditorTests.cs
git commit -m "feat(app): MaintenanceService.SaveTranscriptEditsAsync (corrections + splits)"
```

---

## Phase 4 — App: editor view-models

### Task 10: `EditableSegmentViewModel` + split-at-caret math

**Files:**
- Create: `src/LocalScribe.App/ViewModels/EditableSegmentViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/EditableSegmentViewModelTests.cs` (create)

**Interfaces:**
- Produces: `EditableSegmentViewModel` (one editable sub-row) with `int Seq`, `TranscriptSource Source`, `int PartIndex`, `[ObservableProperty] string EditedText`, `[ObservableProperty] long StartMs`, `bool DerivedStart`, `[ObservableProperty] SpeakerChoice? Speaker`, `string RawText`, `bool IsSplitChild`; and a pure static helper `SplitAt(EditableSegmentViewModel seg, int caret, long segEndMs) -> (SplitPartEdit left, SplitPartEdit right)` that partitions text at `caret` and computes the right part's derived start by character proportion across `[seg.StartMs, segEndMs]`, rounded to 10 ms.
- Consumes: `SplitPartEdit` (Task 9).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/EditableSegmentViewModelTests.cs
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using Xunit;

public class EditableSegmentViewModelTests
{
    [Fact]
    public void SplitAt_PartitionsText_AndEstimatesDerivedStartTo10ms()
    {
        var seg = new EditableSegmentViewModel(seq: 3, source: TranscriptSource.Remote, partIndex: 0,
            editedText: "First half. Second half.", startMs: 15000, derivedStart: false,
            rawText: "First half. Second half.", speaker: null, isSplitChild: false);

        // caret right after "First half." (index 11 of a 24-char string), segment ends at 17000.
        var (left, right) = EditableSegmentViewModel.SplitAt(seg, caret: 11, segEndMs: 17000);

        Assert.Equal("First half.", left.Text.TrimEnd());
        Assert.Equal("Second half.", right.Text.TrimStart());
        Assert.False(left.DerivedStart);
        Assert.Equal(15000, left.StartMs);
        Assert.True(right.DerivedStart);
        // proportion 11/24 * (17000-15000) = 916.6 -> +15000 = 15916.6 -> round to 10ms = 15920.
        Assert.Equal(15920, right.StartMs);
        Assert.Equal(0, right.StartMs % 10);       // 10 ms grid
    }

    [Fact]
    public void SplitAt_RejectsDegenerateCaret()
    {
        var seg = new EditableSegmentViewModel(3, TranscriptSource.Remote, 0, "hello", 15000, false,
            "hello", null, false);
        Assert.Throws<InvalidOperationException>(() => EditableSegmentViewModel.SplitAt(seg, 0, 17000));
        Assert.Throws<InvalidOperationException>(() => EditableSegmentViewModel.SplitAt(seg, 5, 17000)); // end
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~EditableSegmentViewModelTests"`
Expected: FAIL — type/method missing.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/ViewModels/EditableSegmentViewModel.cs
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>One editable transcript segment sub-row in Edit mode (design §3.2). Materialized only
/// while its section is being edited. A split child carries a PartIndex and a derived start; a
/// whole (unsplit) segment has PartIndex 0 and DerivedStart false.</summary>
public sealed partial class EditableSegmentViewModel : ObservableObject
{
    public int Seq { get; }
    public TranscriptSource Source { get; }
    public int PartIndex { get; }
    public string RawText { get; }
    /// <summary>The seeded displayed text (post vocabulary + edits) at BeginEdit. Immutable
    /// baseline for the correction no-op guard — comparing against RawText would misfire when the
    /// vocabulary pass changed the text (a phantom "correction" on a line the human never touched).</summary>
    public string ProjectedText { get; }
    public bool IsSplitChild { get; }
    public bool DerivedStart { get; }
    [ObservableProperty] private string _editedText;
    [ObservableProperty] private long _startMs;
    [ObservableProperty] private SpeakerChoice? _speaker;

    public EditableSegmentViewModel(int seq, TranscriptSource source, int partIndex, string editedText,
        long startMs, bool derivedStart, string rawText, SpeakerChoice? speaker, bool isSplitChild)
    {
        (Seq, Source, PartIndex, RawText, ProjectedText, DerivedStart, IsSplitChild)
            = (seq, source, partIndex, rawText, editedText, derivedStart, isSplitChild);
        (_editedText, _startMs, _speaker) = (editedText, startMs, speaker);
    }

    /// <summary>Partition this segment's text at the caret into two parts (design §3.3). The left
    /// part keeps this segment's start; the right part's start is estimated by character
    /// proportion across [StartMs, segEndMs] and snapped to a 10 ms grid. Throws on a degenerate
    /// caret (start/end) that would produce an empty child.</summary>
    public static (SplitPartEdit Left, SplitPartEdit Right) SplitAt(EditableSegmentViewModel seg,
        int caret, long segEndMs)
    {
        string text = seg.EditedText;
        if (caret <= 0 || caret >= text.Length || string.IsNullOrWhiteSpace(text[..caret])
            || string.IsNullOrWhiteSpace(text[caret..]))
            throw new InvalidOperationException("split point would create an empty part.");

        double proportion = (double)caret / text.Length;
        long raw = seg.StartMs + (long)Math.Round(proportion * (segEndMs - seg.StartMs));
        long derived = (long)Math.Round(raw / 10.0) * 10;            // 10 ms grid
        derived = Math.Clamp(derived, seg.StartMs + 10, segEndMs);   // stay strictly after the start

        var left = new SplitPartEdit(text[..caret], seg.StartMs, seg.DerivedStart,
            seg.Speaker?.ParticipantId, seg.Speaker?.ClusterKey);
        var right = new SplitPartEdit(text[caret..], derived, DerivedStart: true,
            seg.Speaker?.ParticipantId, seg.Speaker?.ClusterKey);
        return (left, right);
    }
}
```

> This introduces `SpeakerChoice` (a small UI record) — defined in Task 12. To keep this task self-contained for its own test, add a minimal placeholder now and let Task 12 flesh it out, OR define `SpeakerChoice` in this task. Define it here:

```csharp
// src/LocalScribe.App/ViewModels/SpeakerChoice.cs
using LocalScribe.App.Services;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable speaker in the Edit-mode dropdown (design §4). Wraps a display label
/// and the resolved target: a participant id or an existing cluster key. Null of both = "no
/// override" (a split child inherits the parent seq's name).</summary>
public sealed record SpeakerChoice(string Display, string? ParticipantId, string? ClusterKey)
{
    public SpeakerPinTarget? ToPinTarget() =>
        ParticipantId is not null ? new SpeakerPinTarget.Participant(ParticipantId)
        : ClusterKey is not null ? new SpeakerPinTarget.Cluster(ClusterKey)
        : null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~EditableSegmentViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/EditableSegmentViewModel.cs src/LocalScribe.App/ViewModels/SpeakerChoice.cs tests/LocalScribe.App.Tests/EditableSegmentViewModelTests.cs
git commit -m "feat(app): EditableSegmentViewModel + split-at-caret 10ms estimate"
```

---

### Task 11: `EditableSectionViewModel` — expand-on-edit + split/revert

**Files:**
- Create: `src/LocalScribe.App/ViewModels/EditableSectionViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/EditableSectionViewModelTests.cs` (create)

**Interfaces:**
- Consumes: `DisplayRow`/`RowSegment` (Core), `EditableSegmentViewModel`, `SplitPartEdit`, `SpeakerChoice`.
- Produces: `EditableSectionViewModel` wrapping one `DisplayRow` with `[ObservableProperty] bool IsEditing`, `ObservableCollection<EditableSegmentViewModel> Segments` (materialized on first `BeginEdit()`), `void BeginEdit(string timestampsMode, DateTimeOffset startedAt)`, `void SplitSegment(EditableSegmentViewModel seg, int caret)` (replaces `seg` with its two halves), `void RevertSplit(int seq)` (collapses that seq's children back to one segment — signals removal via `SplitRevertsRequested`), and `IReadOnlyList<SplitEdit> CollectSplits()` / `IReadOnlyDictionary<int,string> CollectCorrections()` for the save batch.

- [ ] **Step 1: Write the failing test**

```csharp
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
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~EditableSectionViewModelTests"`
Expected: FAIL — type missing.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/ViewModels/EditableSectionViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

/// <summary>One section row in Edit mode (design §3.2). Read-only until BeginEdit materializes its
/// constituent segments as EditableSegmentViewModels; splitting replaces a segment with two
/// halves. Edit state lives here (on the VM), so the virtualized list can recycle containers
/// safely — the template is data-triggered off IsEditing.</summary>
public sealed partial class EditableSectionViewModel : ObservableObject
{
    public DisplayRow Row { get; }
    public ObservableCollection<EditableSegmentViewModel> Segments { get; } = new();
    [ObservableProperty] private bool _isEditing;

    private readonly HashSet<int> _splitReverts = new();

    public EditableSectionViewModel(DisplayRow row) => Row = row;

    public void BeginEdit(string timestampsMode, DateTimeOffset startedAt)
    {
        if (IsEditing) return;
        Segments.Clear();
        foreach (var s in Row.Segments)
            Segments.Add(new EditableSegmentViewModel(s.Seq, s.Source, s.PartIndex,
                s.ProjectedText, s.StartMs, derivedStart: s.PartIndex > 0, s.RawText,
                speaker: null, isSplitChild: s.IsSplitChild));
        IsEditing = true;
    }

    public void SplitSegment(EditableSegmentViewModel seg, int caret)
    {
        int i = Segments.IndexOf(seg);
        if (i < 0) return;
        long segEndMs = i + 1 < Segments.Count ? Segments[i + 1].StartMs : Row.EndMs;
        var (left, right) = EditableSegmentViewModel.SplitAt(seg, caret, segEndMs);
        Segments[i] = ToSegment(seg.Seq, seg.Source, i, left, seg.RawText);
        Segments.Insert(i + 1, ToSegment(seg.Seq, seg.Source, i + 1, right, seg.RawText));
        Reindex();
    }

    public void RevertSplit(int seq)
    {
        _splitReverts.Add(seq);
        // Collapse this seq's children back to a single read-of-machine-original segment.
        for (int i = Segments.Count - 1; i >= 0; i--)
            if (Segments[i].Seq == seq && Segments[i].PartIndex > 0) Segments.RemoveAt(i);
        Reindex();
    }

    /// <summary>Splits to persist: any seq that now has >1 part in this section.</summary>
    public IReadOnlyList<SplitEdit> CollectSplits()
        => Segments.GroupBy(s => s.Seq)
            .Where(g => g.Count() > 1)
            .Select(g => new SplitEdit(g.Key, g.First().Source,
                g.OrderBy(s => s.PartIndex).Select(s => new SplitPartEdit(
                    s.EditedText, s.StartMs, s.PartIndex > 0,
                    s.Speaker?.ParticipantId, s.Speaker?.ClusterKey)).ToList()))
            .ToList();

    public IReadOnlyCollection<int> CollectSplitReverts() => _splitReverts.ToList();

    /// <summary>Corrections to persist: unsplit segments whose text changed from the seeded
    /// projected text. Comparing against ProjectedText (not RawText) matches the Stage 6.1
    /// correction dialog's no-op guard, so a vocabulary-only difference never writes a phantom edit.</summary>
    public IReadOnlyDictionary<int, string> CollectCorrections()
        => Segments.Where(s => !s.IsSplitChild)
            .GroupBy(s => s.Seq).Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .Where(s => s.EditedText.Trim() != s.ProjectedText.Trim())
            .ToDictionary(s => s.Seq, s => s.EditedText.Trim());

    private static EditableSegmentViewModel ToSegment(int seq, Core.Model.TranscriptSource source,
        int partIndex, SplitPartEdit part, string rawText)
        => new(seq, source, partIndex, part.Text, part.StartMs, part.DerivedStart, rawText,
            speaker: null, isSplitChild: true);

    private void Reindex()
    {
        for (int i = 0; i < Segments.Count; i++)
        {
            var s = Segments[i];
            if (s.PartIndex != i)
                Segments[i] = new EditableSegmentViewModel(s.Seq, s.Source, i, s.EditedText, s.StartMs,
                    derivedStart: i > 0, s.RawText, s.Speaker, isSplitChild: Segments.Count > 1 || s.IsSplitChild);
        }
    }
}
```

> The `CollectCorrections` baseline vs `RawText` is a simplification for this task's test; Task 13's editor VM passes the real `ProjectedText` so the no-op guard matches the correction dialog's (`text == ProjectedText.Trim()`). Keep the method but let Task 13 thread `ProjectedText` in.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~EditableSectionViewModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/EditableSectionViewModel.cs tests/LocalScribe.App.Tests/EditableSectionViewModelTests.cs
git commit -m "feat(app): EditableSectionViewModel expand/split/revert + batch collectors"
```

---

### Task 12: Speaker candidates + `SpeakerChoice` list on the read VM

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/ReadViewSpeakerChoicesTests.cs` (create)

**Interfaces:**
- Consumes: `_loadedMeta`, `_loadedSpeakers` (already refreshed each load, `ReadViewViewModel.cs:198-199`); `ReassignCandidate` candidate logic (mirror `ReassignSpeakerViewModel.cs:58-76`).
- Produces: `ObservableCollection<SpeakerChoice> SpeakerChoicesFor(TranscriptSource source)` (or a method returning the list) built from the loaded meta/speakers; refreshed on every `ApplyRows`. This is the dropdown source for both whole-section and split-child assignment.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/ReadViewSpeakerChoicesTests.cs
// Build a ReadViewViewModel via the existing read-view test harness (see the Stage 6.1
// ReassignSpeakerViewModel/read-view tests for construction), load a session whose meta has a
// named Remote participant "Ms. Adams", then assert:
//   var choices = vm.SpeakerChoicesForRemote();  // helper exposed for tests
//   Assert.Contains(choices, c => c.Display == "Ms. Adams" && c.ParticipantId is not null);
// (Fill in using the real harness; keep it a single focused assertion.)
```

> Because `ReadViewViewModel` construction needs a `MaintenanceService`, `IDualAudioPlayer`, dispatch, etc., reuse whatever harness the existing read-view tests use. If none exists, this task's unit test may be light; the candidate-building logic is a pure transform you can extract into a `static IReadOnlyList<SpeakerChoice> BuildChoices(SessionMeta meta, Speakers? speakers, TranscriptSource source)` and test THAT directly (recommended — mirrors `ReassignSpeakerViewModel` candidate logic and is trivially unit-testable).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ReadViewSpeakerChoicesTests"`
Expected: FAIL — `BuildChoices`/`SpeakerChoicesForRemote` missing.

- [ ] **Step 3: Write minimal implementation**

Add a pure static builder (place in `SpeakerChoice.cs` or the VM) and expose it from the VM:

```csharp
// e.g. in src/LocalScribe.App/ViewModels/SpeakerChoice.cs
public static class SpeakerChoices
{
    /// <summary>Same candidate rule as ReassignSpeakerViewModel (design §4): same-side NAMED
    /// participants first, then named clusters no participant owns. Plus a leading "(unchanged)"
    /// null-target choice so a dropdown can express "no override".</summary>
    public static IReadOnlyList<SpeakerChoice> Build(SessionMeta meta, Speakers? speakers,
        TranscriptSource source)
    {
        var side = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        var list = new List<SpeakerChoice> { new("(unchanged)", null, null) };
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in meta.Participants)
        {
            if (p.Side != side || p.Kind != ParticipantKind.Named || string.IsNullOrEmpty(p.Name)) continue;
            list.Add(new SpeakerChoice(p.Name, p.Id, null));
            if (p.ClusterKey is string ck) owned.Add(ck);
        }
        if (speakers is not null)
        {
            string prefix = source + ":";
            foreach (var (key, name) in speakers.Names)
                if (key.StartsWith(prefix, StringComparison.Ordinal) && !owned.Contains(key))
                    list.Add(new SpeakerChoice($"{name} (detected voice)", null, key));
        }
        return list;
    }
}
```

Add `using LocalScribe.Core.Audio; using LocalScribe.Core.Model;` to the file. On the VM, expose test seams `internal IReadOnlyList<SpeakerChoice> SpeakerChoicesForRemote() => SpeakerChoices.Build(_loadedMeta!, _loadedSpeakers, TranscriptSource.Remote);` (and a Local twin).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ReadViewSpeakerChoicesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SpeakerChoice.cs src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewSpeakerChoicesTests.cs
git commit -m "feat(app): speaker-choice list builder for Edit-mode dropdown"
```

---

### Task 13: Edit-mode orchestration on `ReadViewViewModel` (enter/save/cancel)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/ReadViewEditModeTests.cs` (create)

**Interfaces:**
- Consumes: `EditableSectionViewModel`, `MaintenanceService.SaveTranscriptEditsAsync` (Task 9), `SaveSpeakerPinsAsync` (existing), `ReloadRowsAsync` (existing).
- Produces on the VM: `[ObservableProperty] bool IsEditMode`; `ObservableCollection<EditableSectionViewModel> EditSections`; `void EnterEditMode()` (builds `EditableSectionViewModel`s from `Rows`, gated on `CanEdit`); `bool CanEdit` (finalized/recovered — from loaded session); `Task SaveEditsAsync(CancellationToken)` (assembles a `TranscriptEditBatch` from every section's `CollectCorrections`/`CollectSplits`/`CollectSplitReverts`, calls `SaveTranscriptEditsAsync`, then `ReloadRowsAsync`, then exits edit mode); `void CancelEdit()` (drops `EditSections`, exits). The correction no-op guard lives in `EditableSegmentViewModel.ProjectedText` (Task 10), so `CollectCorrections` already matches the 6.1 correction dialog.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/ReadViewEditModeTests.cs
// Using the read-view harness + a real temp session (finalized, one Remote segment seq 3
// "First. Second." 15000..17000):
//   await vm.LoadAsync(sid, ct);
//   vm.EnterEditMode();
//   var section = vm.EditSections.Single(s => !s.Row.IsMarker);
//   section.BeginEdit(vm.TimestampsMode, vm.StartedAtLocal);
//   section.SplitSegment(section.Segments[0], caret: 6);
//   await vm.SaveEditsAsync(ct);
//   // reload happened; rows now show two Remote turns for seq 3
//   Assert.False(vm.IsEditMode);
//   var edits = await new EditStore(paths.SessionDir(sid), TimeProvider.System).LoadAsync(ct);
//   Assert.True(edits!.Splits.ContainsKey("3"));
```

> Model this on the existing read-view VM tests (they already construct a `ReadViewViewModel` against a temp session + fake player). If those tests exist under `tests/LocalScribe.App.Tests`, copy their setup; keep this test to the single split-save-reload round trip.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ReadViewEditModeTests"`
Expected: FAIL — edit-mode API missing.

- [ ] **Step 3: Write minimal implementation**

Add to `ReadViewViewModel` (fields, properties, and methods):

```csharp
public ObservableCollection<EditableSectionViewModel> EditSections { get; } = new();
[ObservableProperty] private bool _isEditMode;
public bool CanEdit { get; private set; }   // set in ApplyRows: view.Session.EndedAtUtc is not null

public void EnterEditMode()
{
    if (!CanEdit || IsEditMode) return;
    EditSections.Clear();
    foreach (var r in Rows)
        if (!r.Data.IsMarker) EditSections.Add(new EditableSectionViewModel(r.Data));
    IsEditMode = true;
}

public void CancelEdit()
{
    EditSections.Clear();
    IsEditMode = false;
}

public async Task SaveEditsAsync(CancellationToken ct)
{
    var corrections = new Dictionary<int, string>();
    var splits = new List<SplitEdit>();
    var splitReverts = new HashSet<int>();
    foreach (var sec in EditSections.Where(s => s.IsEditing))
    {
        foreach (var kv in sec.CollectCorrections()) corrections[kv.Key] = kv.Value;
        splits.AddRange(sec.CollectSplits());
        foreach (int seq in sec.CollectSplitReverts()) splitReverts.Add(seq);
    }
    var batch = new TranscriptEditBatch(corrections, [], splits, splitReverts.ToList());
    try
    {
        await _maintenance.SaveTranscriptEditsAsync(SessionId, batch, ct);
        await ReloadRowsAsync(ct);
    }
    catch (Exception ex) { _reporter.Report("Save transcript edits", ex); return; }
    IsEditMode = false;
    EditSections.Clear();
}
```

Set `CanEdit = view.Session.EndedAtUtc is not null;` inside `ApplyRows` (near the `CanDiarise` assignment). Add `using` for the new VM types if needed (same namespace, so none).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ReadViewEditModeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ReadViewViewModel.cs tests/LocalScribe.App.Tests/ReadViewEditModeTests.cs
git commit -m "feat(app): read-view Edit-mode enter/save/cancel orchestration"
```

---

## Phase 5 — UI wiring + roster sync

> **XAML wiring is verified by the smoke runbook, not unit tests** — the codebase does not unit-test XAML event/binding wiring (this is exactly how the context-menu Click-handler bug slipped through). Each of these tasks ends with a build + a targeted manual smoke step. Reuse the `BindingProxy` + command pattern (`SessionsPage.xaml` / the read-view context menu) so commands in style-templated controls actually fire.

### Task 14: Read⇄Edit toggle + editable table in `ReadViewWindow`

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml`, `src/LocalScribe.App/ReadViewWindow.xaml.cs`

**Interfaces:**
- Consumes: `ReadViewViewModel.IsEditMode`, `CanEdit`, `EditSections`, `EnterEditMode`, `SaveEditsAsync`, `CancelEdit` (Task 13); `EditableSectionViewModel`/`EditableSegmentViewModel` (Tasks 10-11).

- [ ] **Step 1: Add the mode toggle + commands (code-behind)**

In `ReadViewWindow.xaml.cs`, add `IAsyncRelayCommand SaveEditsCommand`, `IRelayCommand EnterEditCommand`, `IRelayCommand CancelEditCommand` wired to the VM methods (initialize in the ctor BEFORE `InitializeComponent`, mirroring the Stage 6.1 command fix). Reuse the existing `WindowProxy` `BindingProxy` so the buttons in any templated area reach these.

- [ ] **Step 2: Add the table (XAML)**

Add a second content region bound to `EditSections`, visible when `IsEditMode` is true (the read `ListView` visible when false — use the existing `BoolToVis` converter, with an inverse for one side). The editable rows use a `DataTemplate` for `EditableSectionViewModel`:
- Collapsed state (`IsEditing == false`): Speaker | Time | Text columns (read-only `TextBlock`s), a click gesture → `EnterEditCommand`-style call into `section.BeginEdit(...)` (do this via an input binding or a small code-behind handler that passes `TimestampsMode`/`StartedAtLocal`).
- Expanded state (`IsEditing == true`, `DataTrigger`): an `ItemsControl` over `section.Segments` with, per segment: a `ComboBox` (Task 15), an editable time field (Task 16), and a `TextBox` bound to `EditedText` with `AcceptsReturn="False"`; the `TextBox` handles Enter via a `KeyBinding`/`PreviewKeyDown` in code-behind that reads the caret index and calls `section.SplitSegment(seg, caretIndex)`.
- Header buttons: **Edit** (visible when `!IsEditMode && CanEdit`) → `EnterEditMode`; **Save** + **Cancel** (visible when `IsEditMode`).

Keep the list **UI-virtualized** (`VirtualizingPanel.IsVirtualizing="True"`, `VirtualizationMode="Recycling"`, `ScrollUnit="Pixel"`) as the read list is — safe because all edit state is on the VMs.

- [ ] **Step 3: Build**

Run: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj -c Debug --nologo`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (close any running app first).

- [ ] **Step 4: Smoke — the split round trip**

Launch the app, open the **TEST - Sectioning gaps** session, click **Edit**, click the "Them" row (it expands to segments), place the caret mid-text, press **Enter** → the row splits into two sub-rows with a derived time on the second. Click **Save** → the reader shows the two halves; reopen to confirm persistence. Then re-enter Edit, revert the split, Save → one segment again.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs
git commit -m "feat(app): read-view Edit mode table with expand-on-edit and Enter-to-split"
```

---

### Task 15: Inline speaker dropdown + "Manage speakers…" button

**Files:**
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml`, `src/LocalScribe.App/ReadViewWindow.xaml.cs`, `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`

**Interfaces:**
- Consumes: `SpeakerChoices.Build` (Task 12), `EditableSegmentViewModel.Speaker`, whole-section pin via existing `MaintenanceService.SaveSpeakerPinsAsync`, the existing `_openSessionDetails` callback (`ReadViewWindow.xaml.cs:43`).

- [ ] **Step 1: Bind the ComboBox**

Each segment sub-row gets a `ComboBox` whose `ItemsSource` is the source-appropriate `SpeakerChoice` list (expose `SpeakerChoicesForSource(TranscriptSource)` on the VM; the template picks by the segment's `Source`) and `SelectedItem` bound to `EditableSegmentViewModel.Speaker`. For a split child, a non-null selection writes into the split part on save (already handled by `CollectSplits`). For a **whole (unsplit)** segment, selecting a speaker must pin the section: on save, the editor VM detects unsplit segments whose `Speaker` changed and calls `SaveSpeakerPinsAsync(SessionId, source, [seq], choice.ToPinTarget())` before/after `SaveTranscriptEditsAsync`. Add that loop to `SaveEditsAsync` (Task 13) — extend it here:

```csharp
// in SaveEditsAsync, after SaveTranscriptEditsAsync + before ReloadRowsAsync:
foreach (var sec in EditSections.Where(s => s.IsEditing))
    foreach (var seg in sec.Segments.Where(x => !x.IsSplitChild && x.Speaker?.ToPinTarget() is not null))
        await _maintenance.SaveSpeakerPinsAsync(SessionId, seg.Source, [seg.Seq],
            seg.Speaker!.ToPinTarget()!, ct);
```

- [ ] **Step 2: Add the "Manage speakers…" button**

In the Edit-mode header add a **Manage speakers…** button whose click calls `_openSessionDetails(_sessionId)` (same callback the reassign dialog uses).

- [ ] **Step 3: Build + smoke**

Run: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj -c Debug --nologo` → 0/0.
Smoke: in Edit mode, change a whole row's speaker via the dropdown, Save, confirm the label changed and persists. Click **Manage speakers…** → Session Details opens.

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.App/ReadViewWindow.xaml src/LocalScribe.App/ReadViewWindow.xaml.cs src/LocalScribe.App/ViewModels/ReadViewViewModel.cs
git commit -m "feat(app): inline speaker dropdown + Manage speakers button in Edit mode"
```

---

### Task 16: Sub-second (10 ms) split-time editor field

**Files:**
- Create: `src/LocalScribe.App/MsTimestampConverter.cs`
- Modify: `src/LocalScribe.App/ReadViewWindow.xaml`
- Test: `tests/LocalScribe.App.Tests/MsTimestampConverterTests.cs` (create)

**Interfaces:**
- Produces: `MsTimestampConverter : IValueConverter` formatting `long` ms as `mm:ss.ff` / `h:mm:ss.ff` (hundredths) and parsing back to ms on the 10 ms grid; used ONLY on a split child's editable time field (design §3.3). Whole-segment times stay read-only `mm:ss` via the existing stamp path.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/MsTimestampConverterTests.cs
using System.Globalization;
using LocalScribe.App;
using Xunit;

public class MsTimestampConverterTests
{
    [Fact]
    public void Formats_Hundredths()
    {
        var c = new MsTimestampConverter();
        Assert.Equal("00:15.92", c.Convert(15920L, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Parses_BackTo10msGrid()
    {
        var c = new MsTimestampConverter();
        var ms = (long)c.ConvertBack("00:15.92", typeof(long), null!, CultureInfo.InvariantCulture);
        Assert.Equal(15920, ms);
        Assert.Equal(0, ms % 10);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MsTimestampConverterTests"`
Expected: FAIL — converter missing.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.App/MsTimestampConverter.cs
using System.Globalization;
using System.Windows.Data;
namespace LocalScribe.App;

/// <summary>Formats a long-ms split-child start as mm:ss.ff (hundredths; h:mm:ss.ff past an hour)
/// and parses it back onto a 10 ms grid (design §3.3). Editable ONLY on split children; whole
/// segments render whole-second stamps via ReadViewStampConverter.</summary>
public sealed class MsTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long ms) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        string hh = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{hh}{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Accept [h:]mm:ss.ff. Parse leniently; snap to 10 ms.
        var parts = ((string)value).Split(':');
        double seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        int minutes = parts.Length >= 2 ? int.Parse(parts[^2], CultureInfo.InvariantCulture) : 0;
        int hours = parts.Length >= 3 ? int.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
        long ms = (long)Math.Round((hours * 3600 + minutes * 60 + seconds) * 1000);
        return (long)Math.Round(ms / 10.0) * 10;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~MsTimestampConverterTests"`
Expected: PASS.

- [ ] **Step 5: Bind it (XAML) + build**

In the segment sub-row template, for a split child (`DataTrigger` on `IsSplitChild`/`PartIndex > 0`) show a `TextBox` bound to `StartMs` via `MsTimestampConverter` with a subtle "estimated" hint; a whole segment shows a read-only stamp. Add the converter to `Window.Resources`.
Run: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj -c Debug --nologo` → 0/0.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/MsTimestampConverter.cs src/LocalScribe.App/ReadViewWindow.xaml tests/LocalScribe.App.Tests/MsTimestampConverterTests.cs
git commit -m "feat(app): editable 10ms split-time field (mm:ss.ff)"
```

---

### Task 17: Live roster sync from Session Details

**Files:**
- Modify: `src/LocalScribe.App/WindowRegistry.cs` (or wherever cross-window notification lives — verify), `src/LocalScribe.App/ReadViewWindow.xaml.cs`, the Session Details save path, `src/LocalScribe.App/ViewModels/ReadViewViewModel.cs`
- Test: `tests/LocalScribe.App.Tests/RosterChangedTests.cs` (create)

**Interfaces:**
- Produces: a `RosterChanged` notification keyed by `sessionId`. The simplest home is `WindowRegistry` (already injected into `ReadViewWindow`, `ReadViewWindow.xaml.cs:39`): add `event Action<string>? RosterChanged;` and `void NotifyRosterChanged(string sessionId) => RosterChanged?.Invoke(sessionId);`. The Session Details save (participant add/rename/cluster-ownership) calls `NotifyRosterChanged(sessionId)` after its `MaintenanceService.SaveMetaAsync`. `ReadViewWindow` subscribes in the ctor and unsubscribes in `OnClosed` (alongside the existing settings unsubscribe): on a matching id, marshal to the UI thread and call `_vm.RefreshRosterAsync()` which re-runs `ReloadRowsAsync` (rebuilding `_loadedMeta`/`_loadedSpeakers` and thus the dropdown choices) WITHOUT leaving edit mode when possible — if in edit mode, refresh only the choice lists so in-progress edits survive.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/RosterChangedTests.cs
using LocalScribe.App;
using Xunit;

public class RosterChangedTests
{
    [Fact]
    public void NotifyRosterChanged_RaisesEventWithSessionId()
    {
        var reg = new WindowRegistry();
        string? got = null;
        reg.RosterChanged += id => got = id;
        reg.NotifyRosterChanged("s-1");
        Assert.Equal("s-1", got);
    }
}
```

> Verify `WindowRegistry`'s real constructor/namespace and adapt. If `WindowRegistry` is not the right home (e.g. it has no DI lifetime shared with Session Details), put the event on a small injected `IRosterChangeNotifier` singleton registered in `CompositionRoot` and consumed by both windows.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RosterChangedTests"`
Expected: FAIL — `RosterChanged`/`NotifyRosterChanged` missing.

- [ ] **Step 3: Write minimal implementation**

Add the event + notifier to `WindowRegistry` (or the new `IRosterChangeNotifier`). Wire the Session Details save to call it. In `ReadViewViewModel` add:

```csharp
/// <summary>Live roster refresh (design §4): rebuild the loaded meta/speakers (and thus the
/// speaker-choice lists) after Session Details changes the roster, without a reopen. Reuses the
/// gated reload; in edit mode, only the choice lists are refreshed so in-progress edits survive.</summary>
public async Task RefreshRosterAsync(CancellationToken ct)
{
    if (IsEditMode)
    {
        var settings = _settings.Current;
        var view = await _maintenance.RunForSessionAsync(SessionId,
            token => LoadViewAsync(SessionId, settings, token), ct);
        _dispatch(() => { _loadedMeta = view.Meta; _loadedSpeakers = view.Speakers; RaiseSpeakerChoicesChanged(); });
    }
    else
    {
        await ReloadRowsAsync(ct);
    }
}
```

(`RaiseSpeakerChoicesChanged` notifies the dropdown `ItemsSource` — e.g. raise `OnPropertyChanged` for the exposed choice collections, or re-populate an `ObservableCollection`.)

Subscribe in `ReadViewWindow` ctor: `_registry.RosterChanged += OnRosterChanged;` and in `OnClosed`: `_registry.RosterChanged -= OnRosterChanged;` where `OnRosterChanged(string id)` dispatches `if (id == _sessionId) await _vm.RefreshRosterAsync(CancellationToken.None);`.

- [ ] **Step 4: Run test to verify it passes + build**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~RosterChangedTests"`
Then: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj -c Debug --nologo` → 0/0.
Expected: PASS + clean build.

- [ ] **Step 5: Smoke — live sync**

Open a session read view in Edit mode, open **Manage speakers…**, rename/add a participant, Save Session Details → the transcript editor's speaker dropdown shows the new name WITHOUT reopening.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): live roster sync from Session Details into the transcript editor"
```

---

## Phase 6 — Full verification

### Task 18: Full-suite gate + spec delta + smoke runbook

**Files:**
- Modify: `docs/specs/localscribe-specs.md` (§1.6 splits delta + Edit-mode subsection — see design §9)
- Create: `docs/plans/2026-07-07-transcript-editor-smoke-runbook.md`

- [ ] **Step 1: Run the full Core + App suites**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj` then `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`
Expected: Core 366 (+ the split/projection/name-resolver tests) green with only the 2 known fixture fails; App suite green including every new VM/service test.

- [ ] **Step 2: Solution build gate**

Run: `dotnet build LocalScribe.slnx -c Debug --nologo` (close the app first)
Expected: `0 Warning(s) 0 Error(s)`.

- [ ] **Step 3: Write the spec delta**

Update `docs/specs/localscribe-specs.md` §1.6 with the `splits` overlay (schema, invariants, projection expansion, correction precedence, revert, derived-timestamp flag) and add the Edit-mode subsection per design §9.

- [ ] **Step 4: Write the smoke runbook**

Create `docs/plans/2026-07-07-transcript-editor-smoke-runbook.md` with: enter/leave Edit; expand-on-edit; mid-segment split + derived-time adjust to 10 ms; assign each half to a different speaker; whole-row speaker change; revert split; Save/Cancel; reopen persistence; **>1 hr synthetic session** for virtualization + `h:mm:ss` stamps; roster live-sync. No Unicode emojis in any test script referenced.

- [ ] **Step 5: Commit**

```bash
git add docs/specs/localscribe-specs.md docs/plans/2026-07-07-transcript-editor-smoke-runbook.md
git commit -m "docs: transcript editor spec delta + smoke runbook"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** table editor (Tasks 14-16), expand-on-edit (11, 14), mid-segment split overlay (1-3, 8-11), derived 10 ms timestamp (10, 16), assign-only speaker + Manage-speakers button (12, 15), live roster sync (17), long-session virtualization (14, verified in 18 smoke), evidentiary invariants (1-3, 7-8), byte-identity guard (8 step 5, 18 step 1). All design sections map to a task.
- **Two speaker channels (by design):** whole-section speaker → `speakers.json` pin (`SaveSpeakerPinsAsync`); split-child speaker → the split part in `edits.json`, resolved by `NameResolver` overrides. Do not merge them.
- **Correction precedence:** `ApplySplitAsync` clears any `corrections[seq]` (Task 2); `RemoveSplitAsync` does NOT resurrect it (Task 3) — the machine floor is the revert target.
- **Recycling safety:** all Edit-mode state lives on the VMs (Tasks 10-11); templates are data-triggered. Do not stash edit state in the visual tree — that reintroduces the Stage 6.1 recycling footgun.

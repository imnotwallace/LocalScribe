# Import-time speaker-detection follow-ups — design

Date: 2026-07-29
Status: approved (brainstorming), ready for implementation plan
Origin: the four deferred, non-blocking items triaged by the whole-branch review of the
import-time speaker-detection round (merged @ `69adffd`). Ledger:
`.claude/worktrees/import-auto-diarisation/.superpowers/sdd/2026-07-28-import-auto-diarisation/progress.md`
follow-ups 1-4; prior design `docs/superpowers/specs/2026-07-28-import-auto-diarisation-design.md`.

Line numbers below were re-verified against current `master` source by content, not carried from the
merge. They may drift again before implementation — the plan re-verifies each.

## Scope

Four independent fixes, one branch, smallest blast radius each:

1. **[correctness/UX]** A hydrated Split Speakers row seeds its editable `Name` from the raw
   `speakers.json` overlay, not the effective resolved name. After a participant is renamed in
   Session Details, the dialog shows a stale name and a Confirm silently reverts the transcript.
2. **[UX]** A hydrated Split Speakers dialog opens with no source ticked, so a rename-only reopen
   needs an extra click before Confirm enables.
3. **[test]** No end-to-end test asserts a 2-channel downmix import writes the `ImportedDownmixed`
   marker line (only the >2-channel case is covered).
4. **[chore]** Sweep stale comments in `App.xaml.cs` left by the round's field rename and line drift.

None of these change evidentiary behaviour: no transcript content is deleted or rewritten, no
degradation becomes silent, the commit path still never touches audio for any `AudioRetention`, and
no name is ever auto-assigned from a voiceprint match.

---

## Follow-up 1 — hydrated rows must show the effective name, not the raw overlay

### The defect (verified against current source)

`SplitSpeakersViewModel.HydrateClusters` seeds a cluster row's editable `Name` from the committed
`speakers.json` overlay:

```csharp
// SplitSpeakersViewModel.cs (~:556)
if (committed.Names.TryGetValue(clusterKey, out string? name)
    && !string.IsNullOrWhiteSpace(name))
    row.Name = name;
```

But `NameResolver.ResolveClusterKey` (`NameResolver.cs:63-71`) resolves a clusterKey with the
**participant-ownership tier ranked above the overlay**:

```csharp
SessionParticipant? owner = meta.Participants.FirstOrDefault(p =>
    p.ClusterKey == clusterKey && p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name));
if (owner is not null) return owner.Name;                                 // tier 1a — ownership
if (speakers is not null && speakers.Names.TryGetValue(clusterKey, out var named)) return named;  // 1b — overlay
// ... else derived "Speaker N"
```

So the two disagree exactly when a participant is renamed **in Session Details after diarisation**:
that write changes `p.Name` but leaves `speakers.Names` and `p.ClusterKey` untouched. The read view
renders the new name via ownership; the reopened Split Speakers row shows the stale overlay name.

### Why Confirm then reverts

With the stale name in the row, `ConfirmAsync`'s ownership computation (`:923-930`) matches the
row's effective name against `NameCandidates` (that side's Named slots). The stale name matches
**no** candidate — the candidate now carries the new name — so the ownership map drops that
participant. The rename-only path hands `RenameSpeakersAsync` an empty ownership map for the source,
and the Task-7 clear rule (deliberate, so a genuine rename-off-an-owner releases the old owner)
clears `p.ClusterKey`. `NameResolver` then falls through to the overlay tier, and the transcript
reverts to the old name.

Pre-existing, symmetric with (and gentler than) the fresh-run path, non-evidentiary
(`transcript.jsonl` untouched), reversible by retyping. It is nonetheless two defects in one: the
dialog **displays** a name that contradicts the read view, and a Confirm that the user believes is a
no-op **reverts** their rename.

### Fix — a shared owner-then-overlay resolver (approved approach)

Add a public helper to `NameResolver` that returns the owner-tier name, else the overlay name, else
`null`, and route both the existing `ResolveClusterKey` and the new hydration seed through it. This
centralises the precedence ladder in one pure-Core place so the dialog can never again drift from
what the read view renders.

```csharp
// NameResolver.cs — new public helper (behaviour-preserving refactor of the existing tiers 1a+1b)
/// <summary>The owner-then-overlay display name for a clusterKey, or null when neither tier
/// supplies one (design 2026-07-29). Public so Split Speakers hydration seeds a row's editable
/// name from the SAME precedence the read view renders, instead of the raw speakers.json overlay.
/// Deliberately WITHOUT the "Speaker N" derived fallback: a hydrated row keeps its own
/// DefaultSpeakerLabels default (side-prefixed, 1-based), a different string from the 0-based
/// derived label below.</summary>
public static string? ResolveClusterName(string clusterKey, Speakers? speakers, SessionMeta meta)
{
    SessionParticipant? owner = meta.Participants.FirstOrDefault(p =>
        p.ClusterKey == clusterKey && p.Kind == ParticipantKind.Named && !string.IsNullOrEmpty(p.Name));
    if (owner is not null) return owner.Name;
    if (speakers is not null && speakers.Names.TryGetValue(clusterKey, out string? named)) return named;
    return null;
}

private static string ResolveClusterKey(string clusterKey, Speakers? speakers, SessionMeta meta)
{
    if (ResolveClusterName(clusterKey, speakers, meta) is { } name) return name;
    int colon = clusterKey.IndexOf(':');
    string clusterId = colon >= 0 ? clusterKey[(colon + 1)..] : clusterKey;
    return "Speaker " + clusterId;
}
```

`ResolveClusterKey` is byte-for-byte behaviour-preserved — the same owner → overlay → derived order,
including returning an empty overlay value verbatim as it does today.

In `HydrateClusters`, replace the overlay-only seed with the shared resolver, keeping the row's own
default when it returns null or whitespace. `HydrateClusters` has no `SessionMeta` today (it takes
`committed` + `suggestions`); thread `loaded.Meta` in as a new parameter from the single call site in
`Apply` (`:496`) — the same object `_localCandidates`/`_remoteCandidates` are already built from:

```csharp
// SplitSpeakersViewModel.HydrateClusters(Speakers committed, SessionMeta meta, IReadOnlyDictionary<...> suggestions)
if (NameResolver.ResolveClusterName(clusterKey, committed, meta) is { } seeded
    && !string.IsNullOrWhiteSpace(seeded))
    row.Name = seeded;
```

The `IsNullOrWhiteSpace` guard preserves the existing "blank means keep the default" rule, and the
row keeps `DefaultSpeakerLabels.For(source, clusterId)` (`"Local Speaker 1"`) — never the resolver's
0-based derived `"Speaker 0"` — when neither tier applies.

### Why this is correct (both facts verified)

- **Ownership re-asserts.** The candidate lists are built from that side's Named participants
  (`SplitSpeakersViewModel.cs:450-457`), so the owner is always among them. Seeding `row.Name` from
  the owner's name means `ConfirmAsync`'s ownership match (`:927`) finds the candidate again, the
  ownership map keeps `p.ClusterKey`, and nothing is cleared. The revert cannot occur.
- **No wrong default.** `DefaultSpeakerLabels.For` is side-prefixed and 1-based
  (`DiarisationCommit.cs:23`, `"{source} Speaker {id+1}"`), unlike the resolver's 0-based
  `"Speaker {id}"`. Reusing the null-returning helper (not `NameResolver.Resolve` wholesale) keeps
  the row's own default intact.

### Semantic consequence (the reason this needed a design pass)

The hydrated row's committed name is now the **effective resolved name**, not the raw overlay.
Confirming an *untouched* hydration that had drifted therefore writes `speakers.Names` to converge
onto the owner's name and re-asserts ownership. That write is evidentially inert (the read view
already rendered the owner's name via the ownership tier), non-destructive (`transcript.jsonl`
untouched), and leaves the overlay in agreement with meta. This is the intended, benign outcome —
the dialog now tells the truth and a no-op Confirm stays a no-op for the transcript.

Rejected alternative: leave the seed as the raw overlay and instead suppress the ownership clear on
an unchanged Confirm. That masks the revert but leaves the dialog displaying a name that contradicts
the read view — a display lie — so it fixes only half the defect.

### Tests (follow-up 1)

- **`NameResolverTests`** (Core): `ResolveClusterName` returns the owner name when a Named slot owns
  the clusterKey; the overlay name when none does; `null` when neither tier supplies one. Pins the
  precedence the hydration now depends on. Existing `ResolveClusterKey` tests stay green (proving the
  refactor is behaviour-preserving).
- **`SplitSpeakersViewModel` hydration/confirm** (App, queued dispatch fake): the reviewer's
  deterministic repro as the failing test —
  1. A diarised session where participant `p1` owns `Local:0` with `p1.Name == "Sarah Chen"` and
     `speakers.Names["Local:0"] == "Sarah Chen"`.
  2. Rename `p1` to `"Sarah Chen-Smith"` in meta (the Session-Details rename), leaving the overlay
     and `p1.ClusterKey` untouched.
  3. `LoadAsync` → assert the hydrated row's `Name == "Sarah Chen-Smith"` (today: `"Sarah Chen"`).
  4. `ConfirmAsync` (rename-only) → assert on disk that `p1.ClusterKey` is still `"Local:0"`
     (ownership re-asserted, not cleared), `speakers.Names["Local:0"] == "Sarah Chen-Smith"`
     (converged), and `NameResolver.Resolve` on a `Local` segment renders `"Sarah Chen-Smith"`
     (the transcript is not reverted).

---

## Follow-up 2 — auto-select a source that has hydrated rows

### The defect

`SplitSourceOption._selected` defaults to `false` (`SplitSpeakersViewModel.cs:28`) and the load path
adds sources without ticking any (`:392, :396`). `CanConfirm` requires a ticked source
(`:330`, `!IsRunning && Clusters.Count > 0 && Sources.Any(s => s.Selected)`). So a hydrated dialog
opened purely to rename shows the rows but leaves Confirm disabled until the user ticks the source —
an extra click that the whole rename-hydration path exists to avoid. This was an explicit deliberate
default from the round's `I2` fix; the change is narrow.

### Fix

After `HydrateClusters` runs inside `Apply`, tick each source that was actually hydrated — i.e. whose
`Source` is present in `_assignmentBySource` (populated only by hydration at load time, so this is
exactly the hydrated set and is empty on a never-diarised load):

```csharp
// SplitSpeakersViewModel.Apply, immediately after `if (loaded.Committed is { } committed) HydrateClusters(...)`
foreach (var s in Sources)
    if (_assignmentBySource.ContainsKey(s.Source))
        s.Selected = true;
```

Naturally inert for a fresh (never-diarised) load: `_assignmentBySource` is empty, `Clusters` is
empty, and `CanConfirm` stays false as before. Setting `Selected` fires the per-option
`PropertyChanged` wired at `:463`, which re-notifies the Confirm/Run/ForceCount commands.

### Tests (follow-up 2)

- **`SplitSpeakersViewModel` hydration** (App, queued dispatch fake): loading an already-diarised
  session auto-ticks the hydrated source and `CanConfirm` is true with no user interaction; a source
  with no hydrated rows is not ticked; a never-diarised session ticks nothing and `CanConfirm` stays
  false.

---

## Follow-up 3 — end-to-end test for the 2-channel downmix marker

### The gap

`ChannelMapperDownmixMarkerTests` pins `Plan(2, Downmix).Downmixed == true` and the importer's append
path is verified for the >2-channel case
(`AudioImporterTests.Multichannel_downmixes_with_a_note_and_no_claim_means_no_gate`, `:332-357`,
asserting `Markers.ImportedDownmixed, 4`). Their composition for the **2-channel** case — the primary
path this feature serves — is untested end to end.

`AudioImporterTests` runs the real importer against fully in-memory fakes (`FakeDecoder`,
`EchoFactory`, `EnergyProbe`), so this is a **non-fixture** Core test needing no ffmpeg or model
files.

### The test

A new `[Fact]` mirroring the multichannel test but with a 2-channel decoded WAV and
`StereoMapping.Downmix`:

- Decode a 2-channel burst WAV (`WriteBurstWav(name, 16000, 2, 0, 1)`), `ClaimedChannels = 2`, no
  duration claim (so the duration-mismatch marker does not confound the assertion).
- Import with `StereoMapping.Downmix` (the `Request` default).
- Assert the transcript contains a `TranscriptKind.Marker` line equal to
  `string.Format(Markers.ImportedDownmixed, 2)`.
- Assert `session.Sources == [SourceKind.Local]` (one downmixed leg, not a two-leg split) and
  `session.ImportedSource.DecodedChannels == 2`.

The exact `ImportedSource.ChannelMapping` string for the 2-channel downmix case is pinned during
implementation by reading current source; the marker line is the load-bearing assertion.

---

## Follow-up 4 — sweep stale comments in `App.xaml.cs`

Comment-only, zero behaviour change, found by content (line numbers have drifted):

- The comment saying **"importBusy is still null here"** — the captured local was renamed to
  `importLane.BusyReason` (an `ImportLaneState` field). Occurs at the post-import fan-out and,
  pre-existing, near the start of the import runner.
- The comment citing **shifted line numbers** (`:602 / :605 / :581`, now moved) near the
  `SettingsPageViewModel`/engine-gate construction region.

Replace line-number references with the symbol names they point at, so the comments stop rotting.
No test (comments only); the build must stay at 0 warnings.

---

## Files touched

| File | Change | Follow-up |
|---|---|---|
| `src/LocalScribe.Core/Projection/NameResolver.cs` | New public `ResolveClusterName` helper; `ResolveClusterKey` delegates to it (behaviour-preserving) | 1 |
| `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` | Seed hydrated row `Name` via `ResolveClusterName`; auto-select hydrated sources after `HydrateClusters` | 1, 2 |
| `src/LocalScribe.App/App.xaml.cs` | Comment sweep (rename + line-ref rot) | 4 |
| `tests/LocalScribe.Core.Tests/NameResolverTests.cs` | `ResolveClusterName` precedence tests | 1 |
| `tests/LocalScribe.App.Tests/SplitSpeakers*Tests.cs` | The rename-revert repro + the auto-select tests (queued dispatch fake) | 1, 2 |
| `tests/LocalScribe.Core.Tests/AudioImporterTests.cs` | 2-channel downmix marker e2e `[Fact]` | 3 |

## Constraints (locked)

- Evidentiary rules unchanged: no transcript delete/rewrite; no silent degradation; commit path
  never touches audio for any `AudioRetention`; never auto-assign a name from a voiceprint match.
- **New VM tests MUST use a queued dispatch fake** (copy the canonical `QueuedDispatch` with
  `PumpOne`), never synchronous `a => a()` — the assistant-surfaces round shipped a Critical
  stamp-ordering bug a synchronous fake masked.
- **No `System.Progress<T>`** in a VM or test.
- **No Unicode emojis** in any test or tool script.
- Build must be **0 warnings** (run `dotnet clean` before the final gate — analyser warnings only
  surface after a clean build).

## Out of scope

- Tuning the untuned `0.5f` diariser threshold / recording a real-audio DER corpus (awaits smoke
  data, unchanged from the prior round).
- The `Win32Exception`-on-missing-helper hardening (`ProcessDiarisationHelper.cs:33`), still deferred.
- Any change to the ownership-clear semantics themselves — follow-up 1 fixes the *seed*, not the
  clear rule, which is deliberate and pinned.

## Testing summary

- **Core (non-fixture):** `NameResolverTests` (new precedence cases + existing green),
  `AudioImporterTests` (new 2-ch marker case).
- **App:** `SplitSpeakers*Tests` (rename-revert repro, auto-select), all with a queued dispatch fake.
- **Gate before merge:** `dotnet clean` then a 0-warning build; Core (non-fixture) / App / Mcp green;
  whole-branch review. Known flaky, re-run before treating as a regression:
  `SessionsPageViewModelTests.Stop_upserts_the_just_stopped_row...` (collection-modified race).

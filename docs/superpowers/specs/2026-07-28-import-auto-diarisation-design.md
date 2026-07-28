# Import-time speaker detection (auto-diarise imported audio) — design

Date: 2026-07-28
Status: approved (brainstorming), ready for implementation plan
Origin: user request — "imported mono/downmixed audio has no speaker labels unless I manually
open Split Speakers afterward"

## Problem

`AudioImporter.ImportAsync` runs `Copy → Decode → ChannelMap → Transcribe → Save`
(`AudioImporter.cs:101/133/147/166/181`) and never diarises. The only call to
`IDiarisationEngine.DiariseAsync` in `src/` is `SplitSpeakersViewModel.cs:452`.

The gap is **wider than mono**. `ChannelMapper.Plan` (`ChannelMapper.cs:24-35`) collapses mono,
2-channel `Downmix`, and every `>2`-channel file into a single `LegPlan(SourceKind.Local, null)`,
and `StereoMapping.Downmix` is the **default** on `ImportRequest` (`AudioImporter.cs:20`). Every
segment on that leg is then stamped `SpeakerLabel = "Me"` (`TranscriptMerger.cs:26-30`, the only
writer of that field), so an entire imported two-party call renders as one speaker called "Me".

The manual fallback does not actually work on a fresh import. `SplitSpeakersViewModel.LoadAsync`
gates each side on `meta.LocalCount > 1` (`SplitSpeakersViewModel.cs:343`, `:347`);
`SessionMeta.LocalCount`/`RemoteCount` default to `1` (`SessionMeta.cs:21,24`) and
`AudioImporter` calls `SessionBootstrap.StartAsync` without ever setting them
(`AudioImporter.cs:108-110`). The dialog therefore opens with zero diarisable sources and `Run`
disabled, unless the user first declares 2+ participants in Session Details.

Separately, reopening Split Speakers on an already-diarised session re-runs the **entire**
diarisation: `Clusters` is cleared on load (`:402`) and repopulated only inside `RunAsync`
(`:497-506`). There is no hydrate-from-disk path, so renaming a speaker costs minutes of CPU.

## Goal

1. Offer speaker detection at import time for single-leg imports, on by default.
2. Commit the result so the transcript improves immediately, then let the user name the speakers
   without paying for a second diarisation.
3. Never let a detection failure cost a completed transcription.

## Decisions (locked during brainstorming 2026-07-28)

| Decision | Choice |
|---|---|
| Count input | One control: `Don't detect speakers` / **`Detect automatically`** (default) / `2`–`6`. Auto feeds `ForcedClusterCount = null`; a number feeds `ForcedClusterCount = n` |
| When it runs | On by default for **single-leg** imports (mono, 2-ch downmix, >2-ch). Not offered when "each party is on their own channel" is ticked |
| Commit timing | Import commits default labels (`"Local Speaker 1/2/…"`), then completion opens Split Speakers **pre-loaded from what was just committed** — no second diarisation run |
| Architecture | **App-layer orchestration after `ImportAsync` returns** (approach A) |
| Helper deployment | Pre-flight availability gate **and** `tools/verify-diarizer.ps1` + publish layout guard |
| Engine gate | Close all three holes: import-detect, manual Split Speakers, voiceprint backfill scan |
| `≤1` cluster | Commit nothing; record a marker; open the read view |
| 2-ch downmix marker | Included (see adjacent fix 6) |

## Verified code facts this design relies on

Confirmed by reading the source, not assumed.

- **Auto-detection exists and is real** — `DiarisationRequest(string FlacPath, SourceKind Source,
  string SegmentationModelPath, string EmbeddingModelPath, int? ForcedClusterCount,
  bool EmitEmbeddings = false)` (`IDiarisationEngine.cs:11-15`), and the runner branches:
  ```csharp
  if (forcedClusterCount is int k && k > 0) config.Clustering.NumClusters = k;
  else config.Clustering.Threshold = 0.5f;     // SherpaDiarisationRunner.cs:23-26
  ```
- **The branch is `k > 0`, not null-ness.** A `0` or negative silently takes the auto path while
  the caller believes it forced a count. Nothing upstream validates. This design makes that
  unreachable by construction (see §2).
- **The `0.5f` threshold has never been validated against real speech.** No settings key, no wire
  field, no env var. The only run on record is a synthetic tone clip
  (`docs/plans/2026-07-04-stage-5-spike-notes.md:257,265-266`) which collapsed to **one** cluster;
  the fixture corpus `models/diar-fixture/` was never recorded and
  `tests/LocalScribe.Core.Tests/DiarisationFixtureTests.cs` is opt-in and throws
  `FileNotFoundException`. **The `≤1` handling in §5 exists because of this.**
- **`MaintenanceService.SaveDiarisationAsync` is a proven headless commit API**
  (`MaintenanceService.cs:464-568`), driven with no VM, no dispatcher and no window in
  `MaintenanceServiceDiarisationTests.cs:41,67`. Under the per-session semaphore
  (`RunForSessionAsync`, `MaintenanceService.cs:63-70`) it writes, in order:
  `versions/<v>/speakers.json` via `SpeakersMerge` (`:485-488`), meta.json participant `ClusterKey`
  ownership (`:492-510`), `versions/<v>/embeddings.json` under post-remap keys (`:520-552`),
  `session.Diarised = true` (`:554-558`), then `RegenerateProjectionsAsync` (`:562`) and
  `RaiseSessionContentChanged` outside the gate (`:565-568`). It **never touches
  `transcript.jsonl`, `edits.json`, or audio** for any `AudioRetention` value (`:560-561`,
  regression-pinned across `keep`/`never`/`days:30`).
- **The retained import leg is already the diariser's exact input format.** The diariser demands
  16 kHz / mono / 16-bit, enforced at read with `InvalidDataException → BAD_AUDIO`
  (`FlacPcmReader.cs:23-25,42-44`), and does no resampling. `ChannelMapper.WriteLegs` produces
  16 kHz mono, and `OfflinePipelineRunner.cs:193-204` retains it as FLAC when
  `AudioRetention != "never"`. **No conversion step is needed.**
- **`AudioImporter` destroys the session folder on any throw** —
  `catch { Directory.Delete(_paths.SessionDir(sessionId), recursive: true); throw; }`
  (`AudioImporter.cs:205-210`). A `DiarisationException` raised inside that `try` would delete a
  fully transcribed, fully provenanced import.
- **The Save stage clobbers concurrent session.json writes.** The `record with { … }` at
  `AudioImporter.cs:185` operates on a snapshot read at `:183`, so any session.json field written
  between those lines is overwritten — including `Diarised`.
- **The helper reports determinate progress.** stdout is NDJSON: zero or more `{"progress":0..1}`
  then exactly one terminal line (`Diarizer/Program.cs:8-11,19`). But the embedding-extraction
  tail emits **no** progress (`Program.cs:61-72`), so a bare bar parks at 100 % looking hung.
- **Cancel is `proc.Kill(entireProcessTree: true)`** (`ProcessDiarisationHelper.cs:34-38`) — no
  cooperative cancel, and no timeout anywhere in the stack.
- **`LocalScribe.Diarizer.exe` is deployed by nothing.** No ProjectReference, no post-build copy,
  no verify script. `App.csproj:32-38` documents that a same-folder copy would overwrite App's
  `onnxruntime.dll` (1.22 → sherpa's 1.24.4) and calls it "actively unsafe"; publishing is a
  manual runbook step (`docs/plans/2026-07-04-stage-5-smoke-runbook.md:46-58`).
- **`ModelPaths.Resolve` is path-only with no existence check**, deliberately (`App.xaml.cs:331`).
  Any availability gate must do its own `File.Exists`.
- **Diarisation is outside the one-engine-at-a-time contract.** `ExternalEngineBusy` is never
  touched by any diarisation code. The contract is a cooperative **probe, not a mutex**, by
  deliberate design (`SessionController.cs:171,:391-395`, pinned by
  `SessionControllerTests.cs:544-566`).
- **`importBusy` already brackets the whole import** (`App.xaml.cs:596-615`) on a `Task.Run`, with
  the post-import fan-out at `:620-626` (`UpsertRowAsync`, `ReindexSessionAsync`,
  `_semanticIndex.Enqueue`, `openReadView(id)`).
- **Diarisation output and channel-split attribution do not collide on disk.** `transcript.jsonl`
  carries `Source` + `SpeakerLabel`; `speakers.json` carries `Assignments[source][seq] = clusterKey`
  + `Names`. `NameResolver`'s precedence ladder (`Projection/NameResolver.cs:17-53`) resolves it:
  a diarisation assignment (tier 1) always beats the `"Me"`/`"Them"` label (tier 3). Uncovered
  seqs (`ClusterAssigner.cs:41`, `if (bestCluster is null) continue;`) fall back to tier 3.
- **The per-import override idiom** is `_settings with { Model = request.Model ?? … }` plus
  nullable init props (`AudioImporter.cs:71-75`, `:21-25`), with a fail-fast presence gate before
  any disk work (`:77-92`).

## Design

### 1. UX — one control in the existing Import dialog

New row in `ImportDialog.xaml`, placed after the stereo block (`ImportDialog.xaml:55-62`) and
before Matters. It must sit **outside** the `IsStereo`-gated panel, because mono/downmix is
precisely the case that needs it.

```
Speakers  [ Detect automatically  v ]
            ├ Don't detect speakers
            ├ Detect automatically      ← default
            ├ 2  ├ 3  ├ 4  ├ 5  └ 6
```

Three non-default states:

| Condition | Presentation |
|---|---|
| "Each party is on their own channel" ticked | Control replaced by the note *"Speakers come from the channels (Me / Them)."* |
| Helper exe or either sherpa `.onnx` missing | Control disabled, reason visible: *"Speaker detection unavailable — LocalScribe.Diarizer.exe not installed."* Import proceeds undiarised |
| Otherwise | Active, defaulting to `Detect automatically` |

The control stays enabled during the import, matching the Model/Language combos, because the
`ImportRequest` is captured at Start.

### 2. Types (additive)

```csharp
// LocalScribe.Core.Import
public enum SpeakerDetection { Off, Auto, Declared }
```

On `ImportRequest`:

```csharp
/// <summary>Import-time speaker detection. Off (the default) keeps every pre-existing caller
/// behaving exactly as before. Auto maps to ForcedClusterCount = null (sherpa threshold
/// clustering); Declared maps to ForcedClusterCount = SpeakerCount.</summary>
public SpeakerDetection SpeakerDetection { get; init; } = SpeakerDetection.Off;

/// <summary>Required (>= 2) when SpeakerDetection == Declared; must be null otherwise.</summary>
public int? SpeakerCount { get; init; }
```

**Validation is load-bearing, not defensive.** `ImportRequest` rejects `Declared` with a null
`SpeakerCount` or a count `< 2`, and rejects a non-null `SpeakerCount` when the mode is not
`Declared`. Because `SherpaDiarisationRunner.cs:23` branches on `k > 0`, an unvalidated `0`
would silently fall through to the untuned auto path while the UI claimed it forced a count.
The dropdown only offers 2–6, so this guard exists purely to keep that landmine unreachable.

`SpeakerDetection.Off` as the record default means every existing `ImportRequest` construction
site in production and tests compiles and behaves identically.

### 3. Flow — approach A, App-layer orchestration

The `importRunner` lambda in `App.xaml.cs` (which already wraps `ImportAsync` and holds
`importBusy`) becomes two-phase:

```csharp
id = await AudioImporter.ImportAsync(req, stageProgress, confirm, ct, transcriptProgress);
if (req.SpeakerDetection is not SpeakerDetection.Off)
    await detectAndCommitSpeakers(id, req, stageProgress, diariseProgress, ct);
return id;
```

Phase 2, in order:

1. Re-run the availability check (`File.Exists` on the exe and both `.onnx` paths). Missing now
   (a TOCTOU race against the dialog-open check) → take the *detection failed* path in §5:
   `Markers.SpeakerDetectionFailed`, error report, read view.
2. Resolve the retained leg with the same probe `SplitSpeakersViewModel.ProbeLeg` uses
   (`:361-370` — retained-list check, then `StoragePaths.AudioFile` preferred format, then the
   other). Already 16 kHz mono FLAC; no conversion.
3. `IDiarisationEngine.DiariseAsync` with the mapped `ForcedClusterCount` and
   **`EmitEmbeddings: true`**, so `embeddings.json` lands during import and the voiceprint
   suggestion chips work when Split Speakers opens — without a second pass.
4. `ClusterAssigner.Assign(lines, result.Segments, SourceKind.Local)`.
5. If `assignment.ClusterKeys.Count <= 1` → §5's one-voice path; otherwise build a
   `DiarisationCommit` with `DefaultSpeakerLabels.For` names (`DiarisationCommit.cs:23-24`) and
   call `MaintenanceService.SaveDiarisationAsync`.
6. Write `meta.LocalCount` truthfully — `Declared(n)` → `n`; `Auto` → the number of clusters
   actually committed. Both inside `RunForSessionAsync` so they serialise with the commit.

**Why this placement, specifically:**

- The import is atomically complete before diarisation begins, so a diariser failure can never
  reach the `Directory.Delete(SessionDir, recursive: true)` catch at `AudioImporter.cs:205-210`.
- It runs after the Save stage, so `SaveDiarisationAsync`'s `Diarised = true` is not clobbered by
  the `record with` snapshot window at `AudioImporter.cs:183-200`.
- No Core→App dependency is introduced: `MaintenanceService` lives in the WPF assembly
  (`namespace LocalScribe.App.Services`) and Core cannot call it. The App layer orchestrates.
- `AudioImporter`'s ctor is untouched, so no Core test construction-site churn.
- `importBusy` extends across both phases by moving one `finally`.

### 4. Progress

`ImportStage` (`AudioImporter.cs:30`) gains `DetectSpeakers`, reported from the App lambda. The
stage order the user sees is `Copy → Decode → Transcribe → Save → Detect speakers`.

Two changes are **mandatory**, or the UI actively lies:

- `ImportDialogViewModel.cs:280-286` has a catch-all `_ =>` that renders any unknown stage as
  *"Saving session…"*.
- `IsTranscribing` is set on `Transcribe` and cleared only on `Save` (`:230-241`), so the stale
  determinate transcription bar and its ETA would otherwise sit on screen through the whole pass.

The bar is determinate, fed by the helper's `{"progress":0..1}` NDJSON through the same
dispatch-marshalled sink pattern as the transcription progress (`ImportDialogViewModel.cs:275-296`).
**House rule: never `System.Progress<T>` in a VM** — it captures a `SynchronizationContext` that
headless tests do not have.

At `1.0` the status text switches to *"Matching voices…"*, because the helper's
embedding-extraction tail emits no progress (`Diarizer/Program.cs:61-72`) and a parked full bar
reads as a hang.

**No ETA.** There is no measured RTF baseline for the diariser anywhere in the repo, so any
estimate would be invented. Percentage only.

Only one leg is ever diarised here (split-stereo is excluded), so the manual dialog's
per-leg `0 → 100 %` reset does not arise.

### 5. Error handling

| Case | Session | Trace | Lands on |
|---|---|---|---|
| Helper/models missing at dialog open | complete, undiarised | none — the disabled control with its reason was visible before Start | read view |
| Detection throws mid-run | **complete and valid** | `Markers.SpeakerDetectionFailed` + `_errors.Report` | read view |
| User cancels during detect | complete, undiarised | none — a choice, not a degradation | read view |
| `Auto` finds ≤ 1 voice | complete, nothing committed | `Markers.SpeakerDetectionOneVoice` + completion notice | read view |
| `SaveDiarisationAsync` throws | complete, transcript intact | `Markers.SpeakerDetectionFailed` + `_errors.Report` | read view |
| Success | diarised | `speakers.json` + `Diarised` flag | **Split Speakers, pre-loaded** |

**Why `≤1` commits nothing.** Labelling an entire call `"Local Speaker 1"` is not an improvement
over `"Me"`, and a collapse to a single cluster is exactly what the untuned `0.5f` threshold did
on the only run on record. Because `SaveDiarisationAsync` never runs, `Diarised` stays `false` and
nothing else would record that detection happened — hence the marker. On the success path a marker
would be redundant (`speakers.json` and the `Diarised` flag are the record), so there is none.

**Markers appended after Save make `MarkerCount` stale.** The Save stage recounts markers into
session.json at `AudioImporter.cs:185-200`; anything appended afterwards is not counted. Both
failure markers therefore append **and** correct `MarkerCount`, inside
`MaintenanceService.RunForSessionAsync` so the write serialises on the per-session semaphore with
everything else touching that session.

On the `Declared(n)` failure path, `meta.LocalCount = n` is still written — the user asserted it,
and it pre-configures the force-N button for a manual retry.

Cancellation during phase 2 cannot propagate into anything that deletes the session, because
phase 2 is outside `ImportAsync` entirely.

### 6. Completion behaviour

`importVm.Completed` (`App.xaml.cs:620-626`) currently runs `UpsertRowAsync`,
`ReindexSessionAsync`, `_semanticIndex.Enqueue`, `openReadView(id)`.

It gains one branch: when speakers were committed, `openSplitSpeakers(id)` opens **on top of** the
read view. Closing it reveals the transcript with the names applied. Every other path is unchanged.

This makes adjacent fix 2 (`NotifyRosterChanged`) mandatory rather than optional — see §8.

### 7. Split Speakers hydration

`LoadAsync` gains a path that rebuilds `Clusters` from the committed `versions/<v>/speakers.json`
**with no engine call**:

- Cluster rows from `Assignments[source]` grouped by `clusterKey`; names from `Names`; previews and
  `snippetStartMs` derived from `_lines` exactly as `RunAsync` does (`:466-477`).
- Suggestions from `embeddings.json` through the existing `VoiceprintMatcher`
  (`VoiceprintMatcher.cs:16-17`, thresholds 0.55 / 0.05), same as the post-run path.
- `Run` stays available to re-diarise from scratch.

**This is the fiddliest task in the build.** `ConfirmAsync` (`:655-739`) reads `_resultBySource`
and `_assignmentBySource`, which only `RunAsync` populates. Hydration must reconstruct the
equivalent from disk so that confirming a hydrated rename goes through the **same single write
path** — `SaveDiarisationAsync` — as confirming a fresh run. `speakers.json` *is* the assignment,
so `_assignmentBySource` reconstructs directly; a rename-only commit must not rewrite
`embeddings.json` with stale vectors, so the existing file is preserved rather than regenerated.

`Confirm` remains the voiceprint consent gate: nothing enrolls, and no `SuggestionProvenance` is
written, until the user presses it. The import's automatic commit writes default labels only —
it never accepts a suggestion, never assigns a name, and never enrolls a voiceprint.

### 8. Adjacent fixes

All are pre-existing defects that this feature touches or amplifies.

1. **Source-gate relaxation.** `SplitSpeakersViewModel.cs:343,:347` becomes
   `if (local is not null)` / `if (remote is not null)` — offer the leg whenever its audio exists.
   `meta.LocalCount`/`RemoteCount` are retained purely as the forced-count value, with the force-N
   button suppressed at `DeclaredCount <= 1`. This fixes imports and hand-recorded sessions alike,
   permanently, without writing a count nobody asserted. **Required** — without it, the manual
   path stays broken and the `≤1` recovery route does not exist.
2. **`NotifyRosterChanged` on Split Speakers confirm.** `App.xaml.cs:422` wires
   `detailEditor.Saved += comp.Windows.NotifyRosterChanged`, but the `openSplitSpeakers` lambda
   (`:342-347`) has no equivalent, so an open read view shows stale speaker names after any
   commit. Since §6 opens the read view and then Split Speakers on top of it, this fires on every
   diarised import. One line.
3. **Engine gate — all three holes.** Register diarisation on `ExternalEngineBusy` so import's
   detect phase, the manual Split Speakers run, and the voiceprint backfill scan
   (`SettingsPageViewModel.cs:966-977`, currently running with `CancellationToken.None`) all
   refuse during a live recording. Implemented as **probe-and-refuse**, not a latch — the
   cooperative-probe contract is deliberate and pinned (`SessionControllerTests.cs:544-566`).
   Contention is CPU/RAM only: the diariser sets no Provider/GPU field
   (`SherpaDiarisationRunner.cs:20-26`), so there is no VRAM contention with whisper — but CPU
   theft can spuriously trip whisper's RTF downgrade ladder (`TranscriptionWorker.cs:121-134`).
4. **`importBusy` visibility.** `App.xaml.cs:579-615` — a non-volatile captured local written on a
   `Task.Run` thread and read from `StartAsync` on another. Its lifetime is being extended across
   a second phase, so it is fixed here.
5. **`tools/verify-diarizer.ps1` + publish layout guard** covering `LocalScribe.Diarizer.exe` and
   both sherpa models, matching what the assistant and MCP helpers already have. Constraint from
   `App.csproj:32-38`: this must be a **sibling-folder** publish like the assistant helper, never
   a same-folder copy — that would overwrite App's `onnxruntime.dll` 1.22 with sherpa's 1.24.4.
6. **2-channel downmix marker.** `Markers.ImportedDownmixed` fires only at `decodedChannels > 2`
   (`ChannelMapper.cs:34`, `DownmixedMultichannel: decodedChannels > 2`), so a stereo two-party
   call imported without ticking the box silently becomes one mixed mono leg with nothing
   recording it. That is now the primary path this feature serves, and silent degradation is
   against house rules.

   The fix is one token — `decodedChannels > 2` becomes `> 1`. The existing marker text
   (`Markers.cs:48-49`, *"imported audio downmixed to mono: source had {0} channels"*) already
   reads correctly for 2. The `ChannelMapPlan.DownmixedMultichannel` field name and its doc
   comment (`ChannelMapper.cs:14-16`) become inaccurate and are renamed to `Downmixed`; the
   comment on `Markers.cs:45` is updated to match.

### 9. Testing

Pure-first, per house style. **Queued** dispatcher fakes throughout — synchronous fakes mask
exactly the `BeginInvoke` stamp-ordering bug the assistant-surfaces round caught. **No Unicode
emojis in any test script** (global rule).

**Core (`tests/LocalScribe.Core.Tests`)**

- `ImportRequest` rejects `Declared` with a null count, and with a count `< 2`; rejects a non-null
  `SpeakerCount` when the mode is not `Declared`. *This is the test that keeps the
  `SherpaDiarisationRunner.cs:23` `k > 0` landmine unreachable.*
- `SpeakerDetection.Off` is the record default and existing `ImportRequest` construction sites are
  unaffected.

**App (`tests/LocalScribe.App.Tests`)**

- `ImportDialogViewModel`: dropdown contents; defaults to `Auto`; suppressed with the note when
  the stereo-split box is ticked; disabled with a reason when the availability check reports
  unavailable; writes mode and count onto the built `ImportRequest`.
- Two-phase runner: success commits and routes to Split Speakers; a thrown
  `DiarisationException` leaves the session folder and transcript intact, appends
  `Markers.SpeakerDetectionFailed`, **corrects `MarkerCount`**, and routes to the read view;
  cancellation during detect keeps the import; a `≤1`-cluster result commits nothing, markers, and
  routes to the read view.
- `meta.LocalCount` is written as `n` for `Declared(n)` (including on the failure path) and as the
  committed cluster count for `Auto`.
- **Hydration with a fake engine asserting zero invocations** — `LoadAsync` on an already-diarised
  session populates `Clusters` from `speakers.json` without calling `DiariseAsync` at all. A test
  that does not assert *absence* of an engine call would not catch a regression to today's
  re-run-everything behaviour. Plus: `Confirm` on hydrated clusters writes names through
  `SaveDiarisationAsync`; suggestions hydrate from `embeddings.json`; `embeddings.json` is not
  rewritten by a rename-only confirm.
- Gate relaxation: a session with `LocalCount == 1` and retained audio offers the leg; the force-N
  button is suppressed at `DeclaredCount <= 1`.
- Engine gate: all three call sites refuse during a live recording.
- `ImportStage.DetectSpeakers` renders its own status text (not the `_ =>` fallback) and clears
  `IsTranscribing`.

**Manual smoke** (cannot be unit tested)

- A real multi-speaker mono import end to end against the real helper — which also produces the
  first RTF data point this repo has for the diariser.
- A stereo-split import: the Speakers control is replaced by the channel note and no diarisation
  runs.
- The unavailable path: rename `LocalScribe.Diarizer.exe`, confirm the control disables with its
  reason and the import completes undiarised.

## Out of scope

- **The `Win32Exception` fix at `ProcessDiarisationHelper.cs:33`.** Spec §8.2
  (`docs/specs/localscribe-specs.md:920`) and `CompositionRoot.cs:126` both claim a missing helper
  surfaces as `DiarisationException(HELPER_CRASH)`, but `Process.Start` on a nonexistent path
  throws an uncaught `Win32Exception`. The pre-flight gate makes this near-unreachable, but a
  TOCTOU race or a corrupt exe still lands there. ~3 lines; deliberately deferred.
- **Tuning the `0.5f` threshold; recording a real-audio DER corpus.** This feature makes it matter
  far more than it did — a follow-up round once smoke data exists.
- Diarising split-stereo legs (each leg is single-speaker by construction when the user's answer
  was truthful).
- A cluster-merge affordance in Split Speakers (typing the same name on two clusters leaves two
  clusterKeys, last-wins, pinned by `SplitSpeakersClusterKeyTests.cs:180-211`).
- A helper timeout / stall watchdog; streaming or chunked diarisation (a leg is one in-memory
  `float[]`).
- Known dead/unwired fields left alone: `Speakers.Confidence` (zero readers, zero writers),
  `ImportedSourceInfo.ChannelMapping` (write-only), `speakers.DiarisedSources` (written by
  `SpeakersMerge.cs:188-199`, read by no UI).
- Background/non-modal import (declined in the 2026-07-22 round and not revisited here).

## Files touched

| File | Change |
|---|---|
| `src/LocalScribe.Core/Import/AudioImporter.cs` | `SpeakerDetection` enum; two `ImportRequest` init props + validation; `ImportStage.DetectSpeakers` |
| `src/LocalScribe.Core/Import/ChannelMapper.cs` | `> 2` → `> 1`; `DownmixedMultichannel` → `Downmixed` (adjacent fix 6) |
| `src/LocalScribe.Core/Model/Markers.cs` | `SpeakerDetectionFailed`, `SpeakerDetectionOneVoice`; `:45` comment update |
| `src/LocalScribe.App/ImportDialog.xaml` (+ code-behind) | Speakers combo, channel note, unavailable reason, detect progress row |
| `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` | Speakers members; availability gate; `DetectSpeakers` stage text; `IsTranscribing` clear; detect progress sink |
| `src/LocalScribe.App/App.xaml.cs` | Two-phase `importRunner`; `importBusy` across both phases + volatility fix; completion branch to Split Speakers; `NotifyRosterChanged` on confirm |
| `src/LocalScribe.App/Services/` (new) | `SpeakerDetectionStep` — probe leg, diarise, assign, commit, `meta.LocalCount`, markers |
| `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` | Source-gate relaxation; hydrate-from-`speakers.json`; engine-gate probe |
| `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` | Engine-gate probe on the voiceprint backfill scan |
| `tools/verify-diarizer.ps1` (new) + publish layout guard | Diarizer.exe + 2 sherpa models |
| `tests/LocalScribe.Core.Tests/*`, `tests/LocalScribe.App.Tests/*` | The tests in §9 |

## Open items for the planning step

- Confirm the exact publish layout-guard mechanism and how it enumerates expected files (the
  assistant deploy uses a guard of ~27 files).
- Confirm the `ImportDialog.xaml` row region and whether the Speakers control shares the grid with
  the Model/Language combos.
- Confirm the shape of the reconstructed `_resultBySource` for hydrated confirms — specifically
  whether `SaveDiarisationAsync` can be called with an empty result set to mean
  "names only, leave `embeddings.json` alone", or whether it needs a narrow rename-only overload.
- Decide the `ExternalEngineBusy` label strings for the three diarisation call sites (existing
  precedent: `importBusy = "audio import"`).

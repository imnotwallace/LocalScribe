# Import resilience, human-authored transcript lines, and a lean export header

**Date:** 2026-08-11
**Status:** design, approved section-by-section by the owner
**Supersedes in part:** `2026-08-05-tier1-hardening-design.md` T1-7 / T1-8 (export metadata surface only —
the integrity seal and `Verify integrity` are untouched)

Three independent changes, bundled into one spec because two of them touch the export renderers
and all three touch the evidentiary story.

**They are three implementation plans, not one.** Part 1 is a crash fix sharing no surface with
the others and can go at any point. Parts 2 and 3 both touch the three renderers, and they couple
in one place: Part 3 deletes the `Human edits` line, while Part 2 introduces a new human act that
would otherwise have to be counted on it.

Recommended order: **1 → 3 → 2.** Part 1 first because it is the reported defect and is currently
destroying real work. Part 3 before Part 2 so the counts line is already gone and Part 2 never
writes code that Part 3 deletes; Part 2 then has exactly one export obligation — flipping the
disclaimer to the "reviewed and corrected" variant, which it must do in either order.

> **Examples in this document use neutral placeholder names.** Real matter and participant names
> from the owner's corpus must never appear in committed files.

---

## Part 1 — An import must never destroy work

### 1.1 What happens today

A 28-minute body-worn MP4 import reaches near-completion and then vanishes, leaving nothing on
disk. Measured, not inferred:

- The source file is sound. Full audio decode completes in 9.7 s with zero errors; the bundled
  ffmpeg produces a 329,832,386-byte WAV at 1717.877 s against a container claim of 1717.921 s —
  a 44 ms difference (0.0026%), far inside the 1% duration gate. Container probe score 100, one
  H.264 stream and one AAC-LC 48 kHz stereo stream.
- The duration gate, the 15-minute ffmpeg timeout, the 4 GiB WAV ceiling and free disk space are
  all ruled out by measurement.

The failure is recorded three times in the owner's diagnostic log
(`{StorageRoot}\diagnostics\diag-202608.jsonl`, 2026-08-07 15:24:02Z / 15:30:55Z / 15:58:03Z,
build `0.9.0+g86e5197`) with one identical stack:

```
System.IO.FileNotFoundException
  at ModelPaths.Require
  at WhisperEngineFactory.CreateAsync
  at TranscriptionWorker.CreateEngineAsync
  at TranscriptionWorker.RecreateAsync
  at TranscriptionWorker.DowngradeAsync
  at TranscriptionWorker.RunAsync
  at OfflinePipelineRunner.RunAsync
  at AudioImporter.ImportAsync
```

Four defects compound to produce it.

**D1 — the model ladder is disk-blind.** `ModelLadder.Downgrade` (`ModelLadder.cs:16-27`) is a pure
name-table step over `["large-v3-turbo", "large-v3", "medium", "small", "base", "tiny"]`. It never
asks whether the next rung's weights exist. `ModelPaths.AvailableModels()`
(`ModelPaths.cs:150-176`) computes exactly that set and `BackendSelector` already consumes it —
but `BackendSelector.Ladder` is a *different* three-element array (`[tiny.en, base.en, small.en]`)
consulted only when the model is `"auto"`. `ModelLadder` is availability-filtered nowhere in
`src/`.

**This is a regression, confirmed by git rather than by the code comment.** Commit `80ab1eb`
("large-v3-turbo is the top downgrade-ladder rung", 2026-08-06 — the 2026-08-05 in the source
comment is the *spec* date) is an ancestor of the failing build `86e5197`. The prior array was
`{ "large-v3", "medium", "small", "base", "tiny" }`, so `Downgrade("large-v3-turbo")` used to
return `null`, and `null` means "at the floor: fall to CPU on the same weights file"
(`TranscriptionWorker.cs:219`) — which survives. Adding turbo to the ladder to give those users a
fallback is exactly what removed their working one.

> The failing build predates the `%LOCALAPPDATA%\LocalScribeModels` download root (`9cea8df` is
> not an ancestor of `86e5197`), so the model set visible at failure time was resolved from a
> different root than today's. That changes nothing: the ladder consults no root at all.

**D2 — a realtime heuristic is armed during offline import.** `LaggingRtfThreshold = 1.0` over
`LaggingWindow = 8` consecutive segments fires a downgrade (`TranscriptionWorker.cs:143-158`).
`OfflineRunOptions.Worker` defaults to `new TranscriptionWorkerOptions()`
(`OfflinePipelineRunner.cs:15`) and `AudioImporter` never sets it (`AudioImporter.cs:215-221`). An
import of a finished file has no realtime constraint; nothing is falling behind. On a long file
this is the likelier of the two paths into the crashing downgrade.

> The OOM trigger is also wider than its name suggests: `WhisperNetEngine.LooksLikeVramOom`
> (`:48-58`) classifies any exception whose message contains "out of memory", *or* contains both
> "CUDA" and "alloc", as a VRAM OOM. Assorted transient CUDA faults therefore route into the same
> crashing downgrade — which is a further reason to make that path survivable rather than to rely
> on the trigger being rare.

**D3 — the downgrade path cannot fail gracefully.** `RecreateAsync`
(`TranscriptionWorker.cs:224-228`) disposes the working engine *before* creating its replacement,
with no `try`/`catch`. Even a handled throw leaves no engine to continue on. The correct pattern
already exists forty lines below: `TrySwapEngineForLanguageLockAsync` (`:262-281`) creates first,
catches, reverts `_plan`, raises `MODEL_DOWNLOAD_FAILED` / `BACKEND_INIT_FAILED` and returns the
still-live `current`. Its own doc names "a missing weight file (e.g. only .en models fetched)" as
the case it handles.

**D4 — the catch-all destroys everything.** `AudioImporter.cs:251-256`:

```csharp
catch
{
    if (sessionId is not null)
        try { Directory.Delete(_paths.SessionDir(sessionId), recursive: true); } catch { }
    throw;
}
```

This window covers the source copy, the decode, the whole transcription run, the retained-FLAC
write and finalize. Any throw inside it costs the entire import.

### 1.2 Owner decision

> **On a fatal transcription error mid-import: keep the session, mark the gap.**

Keep everything transcribed so far and the copied source audio; write a marker at the point it
stopped; offer re-transcription. This is what the worker already does for live capture under the
2026-07-02 "audio is never dropped" ruling — the import path is the outlier.

### 1.3 Design

**Make the ladder disk-aware.** `ModelLadder.Downgrade` gains an availability predicate
(`Func<string,bool>`, injected, so it stays unit-testable without disk) and walks *past* rungs
whose weights are absent. When no rung below is present it returns `null`, which existing code
already reads as "fall to CPU on the current model". Availability must be tested against the file
the factory would actually load — `ModelFileResolver.Resolve` over both roots — not against the
bare `ggml-{name}.bin`, or a quantized-only disk reads as empty.

> Note the `.en` asymmetry: `Downgrade` preserves an `.en` suffix only when the input had one, so
> a walk down from `large-v3-turbo` probes `large-v3`, `medium`, `small`, `base`, `tiny` and will
> *not* match a bundled `base.en` / `tiny.en`. That is correct — switching a multilingual run onto
> English-only weights mid-session is a different decision, already handled by the language-lock
> fix-up. On the owner's current disk the walk therefore finds nothing and falls to CPU on turbo,
> which is the desired outcome.

**Disarm the realtime trigger offline.** `TranscriptionWorkerOptions` grows an explicit switch for
the sustained-RTF trigger; `AudioImporter` and `RetranscriptionRunner` set it off. VRAM-OOM
downgrade stays armed in both modes — that one is a real resource limit, not a pacing heuristic.

**Make recreation survivable.** `RecreateAsync` adopts the `TrySwapEngineForLanguageLockAsync`
shape: build the replacement first, and on failure revert `_plan`, raise the error code, and keep
running on the engine already in hand. A downgrade that cannot find weights becomes a logged
non-event instead of a fatal one.

**Salvage instead of delete.** `AudioImporter`'s catch splits by how far the import got:

| Failure point | Action |
|---|---|
| Before the audio legs are written | Delete the folder as today — nothing worth keeping |
| After the legs exist | Keep the session; append a `TranscriptionFailed` marker carrying the reason; finalize with `EndedAtUtc` set so the recovery scanner does not later adopt it; regenerate projections and reseal |

The salvaged session must be a *complete, valid* session — finalized, sealed, and openable — not a
half-written folder. Re-transcription is already versioned, so the existing "re-transcribe" path
is the recovery route; no new UI is required beyond surfacing the marker.

**Close the two adjacent holes found while measuring:**

- `Directory.CreateDirectory(workDir)` (`AudioImporter.cs:142`) sits *outside* the `try`, so a
  failure there is covered by neither cleanup path. This is the shape of the
  `UnauthorizedAccessException` also present in the log (2026-08-07 13:38:41Z). Move it inside and
  pre-flight the storage root, the temp directory and free space before an ~850 MB copy.
- The cleanup delete at `:254` is itself `try {} catch {}`. A locked file — antivirus still
  scanning the freshly copied source is the obvious case — leaves a folder behind. So a surviving
  folder means "killed, crashed, *or* the cleanup failed", and the recovery scanner will adopt it.
  With salvage in place this ambiguity mostly disappears; the pre-flight covers the rest.

**Log the trigger.** `VRAM_OOM` and `RTF_LAGGING` are raised as `ErrorRaised` events that reach no
log, which is why the log records the crash and not its cause. Wire them to `IDiagnosticLog`, and
log each ladder step taken or refused.

### 1.4 Also in scope (latent, found while measuring)

**Multi-track media is mis-probed.** `FfmpegAudioDecoder` reads only the *first* audio stream
(`:99-110`, `break; // first audio stream only`) while the decode invocation carries no `-map`
(`:36-37`) and lets ffmpeg pick its own best stream. On a file where those differ, the recorded
`decodedChannels` / `decodedSampleRate` / claimed duration describe a stream that was not the one
decoded — silently mis-setting the stereo question, the channel mapping and the duration gate. The
failing file has one audio stream so this did not fire, but Axon Body units ship multi-track
variants and the owner's corpus is Axon body-worn footage. Fix: probe and decode the *same*
explicitly chosen stream (`-map 0:a:{index}`), and record which stream was chosen in
`ImportedSource`.

**Imported sessions never seal their audio.** Both finalize calls omit `sealAudio`
(`OfflinePipelineRunner.cs:225`, `AudioImporter.cs:247`), which defaults to `false`
(`SessionWriter.cs:38`), and `ManifestBuilder.cs:100` then skips any never-sealed leg. Verified on
disk: an imported session's `manifest.json` lists `edits.json`, `meta.json`, `session.json`,
`speakers.json` and `transcript.jsonl` — and not `local.flac`. So **`Verify integrity` makes no
claim about the audio of any imported session.** For a product whose imports are evidentiary
body-worn video, that is a hole in the core promise. Fix: seal imported legs like recorded ones.

> This last item is the one piece of Part 1 that is not required to fix the reported bug. It is
> included because it is a defect in the same code path and directly undermines the evidentiary
> guarantee; it can be dropped without affecting anything else here.

### 1.5 Out of scope

The owner has exactly one model installed, so even a correct ladder has no rung to step to.
Fetching `medium.en` would restore a real fallback. That is a separate decision, deliberately not
bundled with the crash fix.

---

## Part 2 — Authoring a transcript line where the machine heard nothing

### 2.1 What happens today

Insertion is not a disabled button. It is designed out at four independent layers:

1. **No shape for it.** `Edits` holds exactly two maps, `Corrections` and `Splits`, both keyed by
   an existing machine `seq` (`Edits.cs:38-43`). `TranscriptEditBatch` has four legs and no insert
   leg (`TranscriptEditBatch.cs:20-24`). `TranscriptLine` has only `Segment(...)` and
   `Marker(...)` (`TranscriptLine.cs:22-37`).
2. **Every write rejects it.** `EditStore` calls `EnsureSegmentAsync` / `EnsureSegmentsAsync`
   first (`:237-255`, `:272-281`), which throw `ArgumentException("No transcript line with seq
   {seq}.")` when the seq is absent.
3. **The one content-creating gesture is fenced.** Enter-to-split requires every new boundary to
   land strictly inside `(line.StartMs, line.EndMs]` of an existing segment (`EditStore.cs:191-205`).
   By construction, text can only be authored where the machine already found speech — precisely
   the case the owner is blocked on.
4. **Projection cannot render it.** `TranscriptProjection.Build` iterates the JSONL lines
   (`:33-59`). Nothing not derived from a machine line becomes a row.

### 2.2 Owner decisions

> **Disclosure:** the line reads plainly in the exported document; `manifest.json` carries the
> provenance. Consistent with the owner's standing position that human involvement is disclosed by
> the general disclaimer, not per-turn marks.
>
> **Interaction:** both a right-click entry point in read view and an inline `+` affordance in
> edit mode.

### 2.3 Design

**Storage: a third overlay in `edits.json`, keyed by its own generated id.**

```
edits.json
  corrections : seq -> Correction      (existing)
  splits      : seq -> SplitEntry      (existing)
  insertions  : id  -> InsertedLine    (new)

InsertedLine { StartMs, Text, SpeakerRef, AddedAtUtc }
```

`transcript.jsonl` is **not** touched. Three reasons, each load-bearing:

- The evidentiary invariant (spec §1.1) keeps the machine record a pure record of what the machine
  produced. Every human act already lives in an overlay; this is consistent rather than novel.
- A third `TranscriptKind` would break `SegmentCount` + `MarkerCount` arithmetic in **five**
  independent places (`OfflinePipelineRunner.cs:213-214`, `AudioImporter.cs:236-237`,
  `SessionWriter.cs:151-152`, `SessionController.cs:1872-1873`, `SpeakerDetectionStep.cs:196`),
  where a line counted in neither bucket silently stops summing to the line count.
- An older build reading a new enum value throws `JsonException` at
  `LocalScribeJson.cs:30` (no unmapped fallback) and `TranscriptStore.cs:40-45` counts it as
  `malformed++` — the line disappears silently rather than loudly.

**Rendering: `TranscriptProjection.Build` merges insertions into the row stream** before the
existing sort by `(StartMs, SourceRank, Seq)`. Because projection is the single choke point, one
change lights up read view, edit view, `.docx` / `.md` / `.txt`, `session.txt`, search, the
semantic index, the MCP corpus and assistant citations at once.

The three consumers that read raw JSONL lines — `ClusterAssigner`, `VoiceprintEnrollmentService`,
`SplitSpeakersViewModel` — correctly never see an inserted line. It carries no voice evidence to
cluster or enrol from.

**Identity.** `RowSegment`, `SearchLine` and `McpTranscriptRowDto` are all keyed by
`(int Seq, int PartIndex)`, and an inserted line has no machine seq. The representation is:

- All three gain a nullable `InsertionId`. When it is set, the row is human-authored and `Seq` /
  `PartIndex` carry no meaning and must not be read. `McpTranscriptRowDto` already nulls both for
  markers, so its shape is unchanged beyond the new field.
- **No sentinel seq.** No `-1`, no reserved high range — a magic seq would flow into the sort key,
  the speakers key space and the citation format, and would be wrong in all three.
- **Inserted lines never enter `speakers.json`.** The speaker lives on the `InsertedLine` record
  itself (`SpeakerRef`). This sidesteps the assignment key-space collision entirely, and it is
  correct on its own terms: `speakers.json` maps *diarisation clusters* to names, and a
  human-authored line has no cluster.
- **Sort key.** Rows currently order by `(StartMs, SourceRank, Seq)`. Inserted rows take a
  dedicated `SourceRank` so their position against a machine row at an identical `StartMs` is
  deterministic, with `InsertionId` as the final ordinal tiebreak between two insertions sharing a
  timestamp.
- **Citations.** `TranscriptCitation` must render an inserted line without a seq. The plan picks
  the surface form; it must round-trip through the assistant's citation validator.

**Timestamp.** Human-estimated by definition, so it reuses the existing `SplitPart.DerivedStart`
treatment and shows as `(estimated)` wherever a derived start already does. It must fall within
the session duration; it need not fall inside any segment.

**Write path.** Through `MaintenanceService.SaveTranscriptEditsAsync` like every other overlay
write, so the per-session gate holds and `RegenerateProjectionsAsync` reseals `manifest.json`
exactly once. This must be explicit, not assumed: `SpeakerDetectionStep.MarkAsync` (`:184-189`) is
a production path that rewrites two sealed files and deliberately skips projection regeneration,
so "the reseal choke point covers everything" is not true in general.

**Provenance.** `manifest.json` records inserted lines alongside the existing `fabricatedSilence`
disclosure, which is the established pattern for declaring content inside an evidentiary artefact
that the machine did not originate — including its tri-state "known vs none" distinction
(`SessionManifest.cs:44-50`).

**Removal.** An inserted line is human-authored, not machine evidence, so removing one is a revert
of a human act rather than deletion of the record — the same shape as `RemoveSplitAsync`. The
no-deletion rule protects machine output; it does not oblige the owner to keep a typo forever.

**UI.** Read view gains `Add line here…` in the context menu (a dialog seeding the timestamp from
the click position, with a speaker dropdown and a text box), joining the six existing items. Edit
mode gains a thin `+` affordance between rows opening an inline sub-row, saved in the same batch
as every other edit.

---

## Part 3 — A lean export header

### 3.1 What happens today

One metadata block, composed three times in parallel by `DocxRenderer.Write`,
`MarkdownRenderer.Write` and `PlainTextRenderer.Write`, in an order pinned at
`docs/specs/localscribe-specs.md:3605-3610`: title, `App`, `Date`, `Matter(s)`, `Participants`,
`Medium`, `Description?`, `Session ID?`, `Exported?`, `Transcript version`, `Weights file?`,
`Model accuracy?`, `Audio?`, `Audio SHA-256?`, `Transcript SHA-256?`, one `Audio SHA-256 ({leg})`
per sealed leg, `Speakers heard?`, `Human edits?`, in-progress notice, excerpt notice, summary,
disclaimer.

No setting can suppress any of it. `ExportOptions` has five members and no renderer consults any
of them before emitting a metadata line.

Two facts shape the change:

- **`ExportProvenance` is not a file.** No `provenance.json` exists anywhere. It is composed in
  memory per export (`MaintenanceService.ProvenanceFor:1135-1171`) and its only output is these
  header lines. So trimming the header does not weaken `manifest.json` or `Verify integrity` — but
  it does mean the trimmed facts leave no trace in a `.docx`.
- **The `.zip` bundle carries everything anyway.** `SessionArchiver.AddSessionFolderAsync:18-48`
  is a verbatim folder copy excluding only `embeddings.json`, so `session.json` (model, backend,
  weights file, app version), `meta.json` and `manifest.json` (all hashes, all fabricated-silence
  ranges) ride along untouched.

That is the resolution of the tension with T1-7 / T1-8, whose stated purpose is
cross-examination survivability: **the evidentiary archive keeps the full record; the reading copy
gets lean.** Nothing is lost, one artefact stops carrying it.

### 3.2 Owner decisions

> **Header shape:** lean legal header.
> **Speakers:** keep `Speakers heard`, drop `Participants`.
> **Human edits:** drop the four human-act counts; print suppressed duplicates only when non-zero.
> **`[text corrected]`:** delete the per-turn mark entirely, not merely default it off.
> **Human-edit disclosure** moves into the disclaimer as prose.

### 3.3 Design

**Target header:**

```
Interview recording 12 March

Date: 2026-03-12, 1:57 PM – 2:24 PM (27 min)
Matter: <matter display name>
Speakers heard: Speaker A, Speaker B, Speaker C
Medium: Other
Source: interview.mp4

This transcript was generated by automated speech recognition and reviewed
and corrected by a person. It may contain errors and is not a certified record.
```

**Kept:** title, `Date`, `Matter(s)`, `Speakers heard`, `Medium`, `Source`, `Description?`, the
in-progress notice, the excerpt line and notice, the opt-in assistant summary, the disclaimer.

**Removed:** `App`, `Participants`, `Session ID`, `Exported`, `Transcript version`,
`Weights file`, `Model accuracy`, `Audio SHA-256`, `Transcript SHA-256`, every per-leg
`Audio SHA-256`, `Human edits`.

**Three traps in the removal:**

1. **The model name is not its own line.** It is welded into `Transcript version` as
   `"v2 · large-v3-turbo · cuda"` (`MetadataFormat.VersionLine:39-42`). Removing the model means
   deleting that whole line, not editing around it.
2. **`Source` must be built, not merely kept.** Today `Audio: <file>` renders for *imported*
   sessions only (`ImportedSource.FileName`). For a recorded call, the audio file name exists
   **only** inside the label of `Audio SHA-256 (local.flac)` (`MetadataFormat.cs:77`). Deleting
   the hash lines therefore deletes the file name for every recorded session. The new `Source:`
   line must cover both: the original filename for imports, the leg file names for recordings.
3. **Hash and fabricated-silence clause are one unit.** `MetadataFormat.RecordedAudioLines:61-80`
   welds a mandatory clause to every audio hash, because a hash without it certifies
   machine-generated zeros as original recorded audio. They must be removed together; no code path
   may ever emit a hash without its clause.

**Disclaimer, two variants** (replacing `ExportNotices.Disclaimer:10-12`), so it never overclaims:

| Transcript | Text |
|---|---|
| untouched | "This transcript was generated by automated speech recognition and may contain errors. It is not a certified record." |
| human-edited | "This transcript was generated by automated speech recognition and reviewed and corrected by a person. It may contain errors and is not a certified record." |

The variant is chosen from the human-layer counts already computed. A reader cannot tell the text
is conditional; it is simply accurate in both cases. This is what carries the T1-8 anti-concealment
duty, in prose rather than in counts.

**Suppressed duplicates.** Print `Omitted: N duplicate turns (echo)` only when `N > 0`. This is a
machine omission, not a human act, so the disclaimer does not cover it and it is the one count
whose absence the spec calls concealment. Silent on single-leg imports, which suppress nothing.

**`[text corrected]`.** Delete `ExportNotices.CorrectedTurnMark`, `ExportOptions.MarkCorrectedTurns`,
`ExportSetting.MarkCorrectedTurns` and the `ExportDialogViewModel` binding. Settings migration
drops the key; no other consumer exists.

**Not touched:** `meta.json` keeps its participant roster (it drives the speaker UI and the matter
mirror — only the exported document stops printing it); `session.txt`; the save-time
`transcript.md` / `transcript.txt` one-line headers; the docx footer and running head; the `.zip`
bundle.

### 3.4 A second source of technical metadata, deliberately left alone

`[transcription engine: base.en (CPU), Basic accuracy]` is a transcript **marker** at 0 ms
(`Markers.cs:22-30`, `EngineDisclosure.cs:16-21`), as is
`[transcription weights changed: A → B]`. These render in the exported **body**, not the header,
whenever `IncludeMarkers` is true. The owner's current setting is `includeMarkers: false`, so they
are already absent. They stay governed by that existing toggle and are out of scope here — but
they are why model names could reappear in a document whose header has been stripped.

### 3.5 Consequences to accept

- A declared participant who never speaks disappears from the exported document. For body-worn
  footage and interviews this is correct — inaudible people are not in the transcript — but the
  spec called the case out at `:3639` and it is now a deliberate trade.
- A `.docx` alone no longer proves which model produced it. The `.zip` bundle and the session
  folder still do, and `Verify integrity` is unchanged.

---

## Testing

Existing gate: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — 2,553 tests, ~63 s
measured. Do not run with `--no-build` from a stale tree; `BuildVersionTests` derives the expected
version from the props file and will fail against stale binaries.

Per part:

- **Part 1.** Unit tests for the disk-aware ladder walk (including the `.en` asymmetry and the
  quantized-file case, via the injected predicate — no disk). A test that a downgrade with no
  installed rung keeps transcribing rather than throwing. A test that `RecreateAsync` failure
  leaves the original engine live. A salvage test asserting the session survives a mid-run fault
  with source, partial transcript, marker, and a valid seal. A regression test that the
  sustained-RTF trigger never fires on an offline run. The multi-stream fix needs a fixture test
  generating a genuine two-audio-track MP4 — extend `AudioImportFixtureTests`, which already
  synthesises its own media and needs no model weights.
- **Part 2.** Round-trip of an insertion through `edits.json`; projection ordering with an
  insertion between, before and after all machine segments; the three raw-line consumers not
  seeing it; reseal-on-write; revert. Renderer tests for all three formats.
- **Part 3.** Golden-header tests per format asserting the exact kept set and the exact absent
  set. A recorded-session test proving `Source:` survives the hash removal. Disclaimer-variant
  selection. `Omitted:` present at `N > 0` and absent at `N == 0`. Deletion of every
  `MarkCorrectedTurns` surface, plus the settings migration.

## Open items

None blocking. The two flagged optional pieces — sealing imported audio (§1.4) and fetching a
fallback model (§1.5) — are the owner's call and can be dropped independently.

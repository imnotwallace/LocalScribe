# Meetily-round deferred-Minor backlog

**Date:** 2026-07-14
**Context:** The Meetily round (four branches: record-console-polish, retranscription-versions,
audio-import, session-search) was executed subagent-driven with a per-task code review on every
task and a whole-branch review (model: Fable) on every branch. This doc collects the **Minor**
findings those reviews surfaced that were consciously **deferred** — none block anything, all are
merged to master as-is.

**Not in this list (already handled during the round):**
- retranscription-versions **F1** (stale-ActiveVersion overlay bleed — corrections could land in the
  wrong transcript version) — **fixed** (commit `b2b7547`, re-reviewed CONFIRMED).
- retranscription-versions **F2** (runner's `session.json` commit outside the maintenance gate,
  lost-update race) — **fixed** in the same wave.
- retranscription-versions `_runCts` non-`Volatile`, and the `SessionRecord` "schema v3" doc comment
  — **fixed** in the same wave. `TranscriptVersions.NewId` untested — **resolved** (exercised by Task 8).
- audio-import **I-1** (import pipeline ran on the WPF UI thread → long-import freeze) — **fixed**
  (commit `87d5e41`).

Severity of everything below: **Minor** (correctness/evidentiary impact: none, unless flagged). Each
entry: what, where, why it's safe to defer, suggested fix.

---

## Branch 1 — record-console-polish

1. **Floor-fallback backend chip can go stale.** `SessionViewModel` engine chip / `SessionController.ActiveEnginePlan`.
   When a mid-session RTF downgrade happens **at the ladder floor**, the worker flips to CPU
   (`TranscriptionWorker` `DowngradeAsync`) but the live chip keeps the Start-time backend — e.g.
   shows "tiny.en · CUDA" while actually on CPU. *Safe to defer:* the primary silent-CPU-fallback case
   (binds CPU at Start) IS caught by the chip; only the rarer mid-session-at-floor case is stale, and
   `session.json` + the weights marker still record the real engine. *Fix:* expose the worker's
   effective backend as a read-only Core property and bind the chip to it (needs new Core surface,
   which is why it was out of that branch's "read-only over existing signals" constraint).
   **DONE (2026-07-14, chip only):** `TranscriptionWorker.EffectiveBackend` +
   `SessionController.ActiveEngineBackend` + a `FormatEngineChip` backend override; the live chip now
   reflects the fall. **NEW gap surfaced during that work (record, not yet closed — evidentiary, needs
   user sign-off):** the "`session.json` + the weights marker still record the real engine" premise
   above is INCOMPLETE. `PersistFinalAsync` writes `session.json.Backend` from the *Start* plan (unlike
   `Model`, which follows `LastModel`), and `TranscriptionWorker.Adopt` raises the weights-changed
   marker only when the resolved weights FILE changes. `ModelFileResolver` can resolve CUDA and CPU to
   the SAME file (medium/large ship quantized-only, so the higher-fidelity rungs are the likely ones on
   disk in a single variant) — in that case a real CUDA→CPU floor-fall persists NOWHERE once the session
   ends; only the ephemeral chip showed it. *Fix option:* mirror the `Model` pattern at PersistFinalAsync
   (`s.Worker?.EffectiveBackend ?? s.Plan.Backend`), or widen `Adopt`'s marker trigger to compare backend
   as well as file. Left for a user decision because it changes the evidentiary session.json record.

2. **`RemoteSummary` / `MicSummary` are display-orphans.** `RecordingConsoleViewModel`. Their only XAML
   bindings were removed (replaced by the pre-flight line); the properties + ~5 refresh sites + tests
   remain, kept intentionally for `RecordingConsoleViewModelTests`. *Fix:* delete the dead properties
   and their tests in a cleanup pass.

3. **`KeepUpChip(1.04)` renders "Lagging x1.0".** `SessionViewModel.KeepUpChip`. Values just over 1.0
   are correctly flagged lagging but the `0.0` format rounds the number to 1.0. Cosmetic only.

4. **`LiveHardwareProbe.Probe()` memoization is unsynchronized** and now has a second concurrent caller
   (the console's 2 s refresh vs `StartAsync`'s `Task.Run`). Benign: reference assignment is atomic;
   worst case is a duplicate `nvidia-smi` launch computing an identical immutable result. Pre-existing
   class of race, new caller only.

5. **No direct test that a *marker* as the first transcript line clears `ShowListeningHint`.**
   `TranscriptLinesViewModelTests`. The implementation was verified correct by tracing `TranscriptMerger`
   (segment and marker share the `Insert → LineInserted → RebuildFrom` path); only the segment path is
   exercised by a test. Evidentiary-relevant (a capture-degraded-first session should still drop the
   "Listening" hint), so worth a cheap direct test.

6. **`Idle→Recording` raises `ShowListeningHint` twice** (a false→false no-op from `Clear()` before
   `_lastState` updates, then the real false→true). Harmless; WPF tolerates no-op notifications.

---

## Branch 2 — retranscription-versions

1. **Edit-mode speaker-choice list vs save-target version mismatch (from the F1 fix re-review).**
   `ReadViewViewModel.RefreshRosterAsync`. In Edit mode the speaker-choice lists refresh from the
   *current active* version's `speakers.json` while a save correctly targets the *loaded* version; after
   a mid-edit re-transcription, a cluster-only choice could pin a key absent from the target version's
   `speakers.json`. *Impact: label-level only* (renders as "Speaker N"); participant choices are
   version-independent; **no evidence misattribution**. Worth a line in the manual smoke notes.

2. **No VM-level test pins "caller passes `_loadedVersionId`"**, and the read-view version-switcher's
   XAML `IsEnabled` trigger isn't unit-testable in this harness. The property is structurally guaranteed
   (compile-enforced `versionId` param + captured-at-load field), so this is a coverage nicety, not a gap.

3. **No dedicated fault-path test for `RetranscriptionRunner`** (only the cancel path is tested). The
   shared `catch when (!committed)` cleanup is fault-tested in `OfflinePipelineRunnerTests`, giving
   reasonable transitive confidence.

4. **`SetActiveVersionAsync` raises `SessionContentChanged` on an already-active no-op.** (Also see B4 —
   this is the seam consumer's cost.) The core returns `true` without writing when the target is already
   active, but the wrapper raises on any `true`, contradicting the event's "never on a no-op" doc and
   costing a spurious (idempotent) search re-derive + cache rewrite. *Fix:* thread a `wrote` flag out of
   the core, or carve out the no-op case, or fix the event doc.

5. **Several `MaintenanceService` methods re-read `session.json` per call** rather than sharing one read.
   Spec-prescribed; safe under the per-session gate; an extra I/O per call only.

---

## Branch 3 — audio-import

1. **`FfmpegAudioDecoder.RunToolAsync` error/timeout path** throws without awaiting the in-flight
   stdout/stderr `ReadToEndAsync` tasks → a possible silent `TaskScheduler.UnobservedTaskException`
   (non-fatal on modern .NET). *Fix:* catch-and-discard those read tasks on the error/timeout branches.

2. **Cleanup uses a bare `catch {}` around `Directory.Delete`** (`AudioImporter`). A failed cleanup
   leaves an orphan folder — which the startup `RecoveryScanner` picks up the same as a crashed live
   recording (documented in the class doc). Related: decline is signalled by throwing
   `OperationCanceledException` (intentional control-flow reuse), and `SessionId.EnsureUnique` has a
   TOCTOU window under *concurrent* imports (structurally impossible via the single modal import dialog).

3. **`ImportDialogViewModel.LoadMattersAsync` swallows failures to `Debug.WriteLine` only** (no
   `IUiErrorReporter`), unlike every other failure path in that VM → a broken matter-catalog read is
   silently invisible in the import dialog. In a never-silent app this is worth a one-line `_errors.Info`.
   Also: matter-toggle/filter has no test; `PickFileAsync` probes with `CancellationToken.None` (a slow
   or network-mounted file's probe can't be cancelled before it completes).

4. **`PinnedTimeProvider` uses the importing machine's current zone** for the *displayed* / session-id-slug
   local date, not the offset embedded in the entered `RecordedAtLocal`. The canonical `StartedAtUtc` is
   correct (via `ToUniversalTime()`). Only matters if a future date-picker lets the user enter a
   `RecordedAtLocal` whose offset differs from the importing machine's zone (cross-zone import); revisit
   when designing that field's semantics.

5. **One-engine guard fires at dialog-*open*, not import-*start*** (whole-branch M-1). Contrived path:
   tray → open live view → Start recording *after* the import dialog is already open → then click Import →
   both engines run. The reverse direction is solid (`SessionController` refuses Start while `importBusy`),
   and concurrent engines write *disjoint* session folders (resource contention, not record corruption).
   *Fix:* re-check controller/re-transcription busy state inside `runImport` at import start.

6. **WAV-native "decoded truth" is the WAV header** (whole-branch M-2). For an uncompressed PCM WAV the
   decoder reads the input's own `data`-chunk length rather than a separate decode step, so the
   duration-mismatch gate can never fire for WAV, and a **lying WAV header** (data chunk claims more bytes
   than exist) truncates the legs *silently* — the inline comment's "surfaces as a decode error" is
   optimistic (NAudio just returns fewer bytes at EOF). Design accepts native WAV read; *fix later:* a
   bytes-read-vs-expected cross-check in `ChannelMapper.WriteLegs`.

7. **MP4-family container format shows a comma list in the Source column** (whole-branch M-3):
   `SessionRowViewModel` imported branch renders "Imported — MOV,MP4,M4A,3GP,3G2,MJ2" for an `.m4a`
   whose ffprobe `format_name` is the joined list. Cosmetic; pick the first token or map to a friendly
   label.

8. **`openImport` re-resolves `FfmpegLocator.FindToolsDir()` at dialog-open** separately from the
   startup `importAvailable` resolution. Harmless (the button is already gated); the dialog-open value is
   if anything fresher.

---

## Branch 4 — session-search

1. **`SearchQueryEngine` speaker-name fallback emits one hit per matching speaker name.** Two speakers
   whose names share a substring (e.g. "Jane Smith" + "Jane Doe" both contain "jane") produce two hits →
   inflated `HitCount`/ranking; `Distinct` on names is also case-sensitive. Arguably-correct UX (one entry
   per person) but affects ranking; pure-engine, patchable in isolation. Related untested edges: a term
   present in *both* corrected text and original text (correctness rests on code reading), and a
   theoretically-unreachable snippet bound.

2. **`SearchIndexService.InitializeAsync`'s own persist is not internally `try/catch`-wrapped.** A first-run
   cache-write failure would fault the returned `Task` even though `IsReady`/`ReadyChanged` already fired
   and the in-memory index is usable. **Addressed at the call site** (`App.xaml.cs` wraps `InitializeAsync`
   in a fault-swallowing continuation, so startup can't crash), but the Core method itself could be made
   self-guarding for robustness against other callers.

3. **5 of the 11 `SessionContentChanged` raise sites lack a direct test** (`SaveTranscriptEdits`,
   `SaveSpeakerPins`, `RemoveSpeakerPins`, `SaveDiarisation`, `SetActiveVersion`), resting on code-pattern
   analogy; and `SetActiveVersionAsync`'s rich XML doc was split onto the private core when the raising
   wrapper was added. A missed raise self-heals at next app launch anyway.

4. **Read-view find state minor edges:** `UpdateFindStatus` relies on an implicit invariant a future public
   setter of `CurrentFindRowIndex` could break (would silently render "0/n"); reload-survival re-anchors the
   current match by *numeric row index*, not content/seq identity, so a version switch that reorders rows
   could follow different content (never out-of-bounds); and one redundant status update on an early return.
   Also `MoveFindTo`'s doc says a non-match target "leaves the current match untouched" but the code advances
   to the first match after the target (doc drift).

5. **Sessions quick-filter minor edges:** the index-not-ready path pays a needless debounce + `Task.Run`
   round trip instead of the null fast-path; a provably-unreachable defensive snippet ternary; the content
   CTS is never disposed (matches the codebase's existing convention). `ContentFilterTask` is `public`
   (not `internal`) because this repo has no `InternalsVisibleTo` and tests live in a separate assembly.

6. **Search-page presentation gaps:** `SearchResultCard.DateDisplay`/`MattersDisplay` are unasserted by the
   card tests; the `Seq == -1` speaker-name-only click-through ("just open, no target") has no dedicated test.

7. **Vocabulary-save staleness window (by design, whole-branch observation).** A vocabulary save alone
   raises no `SessionContentChanged` and freshness stamps can't observe it, so the read view (live vocab at
   load) can show text that cross-session search can't find until Re-render/Regenerate runs. This exactly
   mirrors the pre-existing on-disk projection staleness and matches design §2.1's seam list — record, not a
   defect.

8. **`SessionsPage` Title column: `DataGridTextColumn` → `DataGridTemplateColumn`** loses that cell's
   implicit clipboard-copy affordance. Negligible.

---

## Suggested triage order (if a polish pass is ever scheduled)

Highest value first (all still Minor):
1. **B3-3** matter-load silent swallow → `_errors.Info` (one line; a silent failure in a never-silent app).
2. **B3-6** WAV lying-header truncation cross-check (the only item with a — narrow — silent-loss shape).
3. **B2-4 / B4-3** the `SessionContentChanged` no-op raise + doc, and the untested raise sites (the search
   seam's correctness contract).
4. **B1-1** worker effective-backend surface for the engine chip (needs new Core surface).
5. Everything else is cosmetic, coverage, or documented-by-design.

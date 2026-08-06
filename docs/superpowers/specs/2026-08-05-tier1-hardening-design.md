# Tier 1 hardening: diagnosability, evidence-loss, trustworthy output, reachability

Date: 2026-08-05
Status: designed, awaiting implementation plans
Input: systematic product review of 2026-08-05 (`.ai-code-review/2026-08-05-product-review.md`,
untracked). That review scored the app across fourteen axes; this spec covers only its **Tier 1**
list — the items it classed as "the product is not credible for a solicitor without these".

## Problem

The domain core scores 6.5-7 against a commercial bar; the product layer around it scores 2-4.
Concretely, six things are true at once today:

- **Nothing is diagnosable.** `DispatcherUnhandledException` sets `Handled = true` with the comment
  "for now, swallow it" (`App.xaml.cs:55`). `LoggingSetting` exists in the settings schema
  (`Settings.cs:29,67`) and no code reads it. There is no log file anywhere in the product. No
  `<Version>` in any csproj and no git tags, so `SessionRecord.AppVersion` is permanently `"1.0.0"`
  on every session ever recorded (`CompositionRoot.cs:67`).
- **An ordinary exit can orphan a recording.** `StopAsync` finalizes audio synchronously, then hands
  the transcript drain *and* the `session.json` `EndedAtUtc`/`DurationMs` write to a background task,
  flips state to `Idle`, and returns (`SessionController.cs:1155-1165`). Nothing on any exit path
  awaits `controller.PendingFinalize` — its only consumers are busy-probes
  (`CompositionRoot.cs:104,163`). The resulting never-ended session goes through recovery, and
  `RecoverIfNeededAsync` never writes `RetainedAudioSources` (`SessionWriter.cs:49-56`), which
  `SessionBootstrap` defaults to `[]`. All three consumers gate on it
  (`ReadViewViewModel.cs:652`, `RetranscriptionRunner.cs:167`, `SplitSpeakersViewModel.cs:431`), so
  valid FLACs on disk become unreachable from playback, re-transcription and Split Speakers.
- **The only editor with no close protection is the one that edits evidence.** `SessionDetailsWindow`
  has a full force-commit Save/Discard/Cancel guard (`SessionDetailsWindow.xaml.cs:75-124`);
  `ReadViewWindow` has no `OnClosing`, no `Closing=` and no `IsDirty`.
- **Mid-recording capture death is unhandled and invisible.** `MicCaptureSource` subscribes only
  `DataAvailable` with no `RecordingStopped` handler (`MicCaptureSource.cs:70,131-140`);
  `SilentLegMonitor` is driven off `PeakObserved` emitted inside the frame loop
  (`LiveSourcePipeline.cs:70`), so zero frames means zero detections. `Markers.PausedSystemSleep` and
  `Markers.AudioDeviceChanged` are declared and written nowhere. No `SystemEvents.PowerModeChanged`,
  no `SessionEnding` handler, no `DriveInfo` anywhere in the solution. `AlignedAudioWriter.PadToMs`
  then silence-fills the FLAC so the file looks the right length.
- **Failures on the two evidentiary actions are invisible.** `Application.MainWindow` is never
  assigned anywhere in the app, so WPF auto-assigns the first `Window` constructed — the
  `OverlayWindow` at `App.xaml.cs:947`. Export/Import/Re-transcribe therefore set `Owner` to the
  recording pill and, being `CenterOwner`, centre on a window that need not have been shown. Their
  error path targets a `MainWindow` InfoBar whose `Severity` is hardcoded `"Error"` and never re-set
  (`MainWindow.xaml:16-17`; `MainWindow.xaml.cs:134-139`), so successes render red. A failed
  `StartAsync` surfaces **only** as a tray balloon (`SessionViewModel.cs:335`), which Focus Assist
  suppresses.
- **The central claim is unfalsifiable in both directions.** `src/LocalScribe.Core/Storage/` contains
  no hashing at all. The only SHA-256 over session data is over an *imported* source
  (`AudioImporter.cs:263-276`), so `ExportProvenance.AudioSha256` is always null for a recorded call.
  `ExportProvenance` omits session id, export timestamp, app version and `WeightsFile` — all four in
  hand at `MaintenanceService.cs:1082-1090`. `DisplayRow.HasCorrection`/`HasPin` exist and no renderer
  reads them, and `PhantomBleedDedup` removes segments from every visible surface including exports
  with no count anywhere (`TranscriptProjection.cs:49-54`).

## Ruling of record: the live model cap is deliberate and stays

The review's largest single finding was that live capture defaults to `small.en` (CUDA) / `base.en`
(Vulkan) per `BackendSelector.cs:45-51` — models this app's own catalog labels "Decent accuracy" and
"Basic accuracy" (`WhisperModelCatalog.cs:28,30`) — while importing the same audio defaults to
`large-v3-turbo`, "Best accuracy at fast speed - recommended" (`ImportDialogViewModel.cs:99`). The
review offered two readings: an unmeasured conservative default, or a deliberate realtime-factor cap.

**Owner ruling, 2026-08-05: it is deliberate. The cap stays.** Live transcription must keep up with
realtime; import can take as long as it likes. This is a correct engineering decision and is not to
be revisited by an implementer.

What follows from the ruling is that the divergence must be **disclosed, not removed**:

- The Start path names the model actually chosen and its catalog accuracy tier, in the UI, before
  recording begins.
- A transcript marker records model + backend at session start, so a transcript carries the fact of
  which engine produced it rather than leaving it in `session.json` only.
- Export metadata surfaces the same fact.
- "Re-transcribe at higher accuracy" is the documented follow-up path for a session that matters,
  since versioned re-transcription already exists.

Two defects found alongside the ruling are **not** covered by it and must still be fixed:

- `ModelLadder.Rungs` is `{large-v3, medium, small, base, tiny}` with no `large-v3-turbo` entry
  (`ModelLadder.cs:7`), so `Downgrade("large-v3-turbo")` returns `null`. A user who explicitly picks
  the catalog-recommended model gets no VRAM-OOM downgrade at all — only a fall to CPU and then an
  indefinite reload loop at the floor (`TranscriptionWorker.cs:106-113,190-198`).
- `_laggingRaised` is never reset (`TranscriptionWorker.cs:121-134`), so the sustained-RTF downgrade
  can fire exactly once per session.

## Scope: ten items, four plans

The review's Tier 1 build order is **make failures visible -> stop losing evidence -> make the output
good -> make the claim provable -> make it obtainable**, and it is not to be inverted: every item
below is validated by observing behaviour that is currently unobservable, so diagnosability comes
first, and shipping an installer comes last so that these fixes are not distributed to people who can
neither diagnose nor update.

The ten items split into four independently mergeable plans.

### Plan A - Diagnosability (`2026-08-05-tier1a-diagnosability.md`)

| Item | Deliverable | Effort |
|---|---|---|
| T1-1 | Real assembly version + git SHA; on-disk rolling diagnostic log behind the existing `IUiErrorReporter` seam; dispatcher exceptions recorded and surfaced instead of swallowed; "Open diagnostics folder" / "Copy last error" in Settings | S |

Log content: dispatcher exceptions, every `Report`/`Info`, session start/stop/recovery, transcription
downgrades, helper process exits, and `ProcessLoopbackCapture.Diagnostic` (today subscribed only by
the SpikeRunner console harness, `SpikeRunner/Program.cs:55`). **Never transcript text** — the log is
a diagnostic artefact a user may send for support, and it must not become an uncontrolled copy of
privileged material.

**Plan A ships first and alone.** Plans B, C and D all write into the logging seam it defines.

### Plan B - Stop losing evidence (`2026-08-05-tier1b-evidence-loss.md`)

| Item | Deliverable | Effort |
|---|---|---|
| T1-2 | Await `PendingFinalize` on every exit path; recovery re-derives `RetainedAudioSources` and `DurationMs`/`EndedAtUtc` from the audio on disk | S |
| T1-3 | Read-view unsaved-changes guard, ported from `SessionDetailsWindow.xaml.cs:75-124` | S |
| T1-4 | Capture-health watchdog: frame-arrival watchdog + leg restart, `OnlyOnFaulted` continuations, disk-space preflight, power/session lifecycle | M |

T1-2 detail: hook `Application.SessionEnding` and the tray Exit path to await
`controller.PendingFinalize` (`SessionController.cs:84`) before `Shutdown()`. In
`RecoverIfNeededAsync`, probe for `local`/`remote` `.flac`/`.wav`, set `RetainedAudioSources` from
what exists, and set `DurationMs`/`EndedAtUtc` from `max(last transcript EndMs,
FlacPcmReader.DurationMs of the longest leg)` — the same probe already used at
`RetranscriptionRunner.cs:191`. **Write a marker when audio outlasts the transcript** so the
discrepancy becomes evidence rather than a silent correction. Add the missing `RetainedAudioSources`
assertion to `SessionWriterTests.Recovery_finalizes_marks_and_appends_marker`, which today asserts
four rewritten fields and not that one — which is why the suite is green over this bug.

T1-4 detail: (a) a per-leg frame-arrival watchdog on the session clock raising `CaptureStalled`,
writing the already-declared `Markers.AudioDeviceChanged`, and re-resolving/restarting the leg the way
`ResumeAsync` already does (`SessionController.cs:894-905`), with `WasapiCapture.RecordingStopped`
subscribed as the fast signal; (b) `OnlyOnFaulted` continuations on the audio and writer loops
mirroring the existing transcription-fault pattern (`SessionController.cs:644-659`), halting the
bridge so the unbounded channel (`CaptureFrameBridge.cs:13-14`) cannot grow after its reader dies;
(c) a `DriveInfo` preflight refusing Start below a hard floor, plus a low-space banner and marker
mid-session; (d) `PowerModeChanged` -> `PauseAsync` + `Markers.PausedSystemSleep`, resume ->
`ResumeAsync` recording the wall-clock gap, `SessionEnding` -> the same stop-then-exit sequence tray
Exit uses.

### Plan C - Trustworthy output (`2026-08-05-tier1c-trustworthy-output.md`)

| Item | Deliverable | Effort |
|---|---|---|
| T1-6 | Model disclosure at Start + transcript marker + export surfacing; `large-v3-turbo` added to `ModelLadder.Rungs`; `_laggingRaised` reset; CPU-floor retry loop capped | M |
| T1-7 | Integrity manifest written at finalize, including the fabricated-silence ranges; "Verify integrity" command | M |
| T1-8 | Complete export provenance and disclose the human layer | S |

T1-7 detail: at finalize write `manifest.json` atomically, recording SHA-256 + size + mtime for each
retained audio leg, `transcript.jsonl`, `edits.json`, `speakers.json`, `meta.json` and `session.json`,
refreshed after every overlay write and at each new version. **Critically it must also record the
sample ranges `AlignedAudioWriter` fabricated** (`AlignedAudioWriter.cs:12,21-34,36-52` inserts
machine-generated zeros for every clock gap and appends zeros to the session end). A hash that seals
that file without recording the fabricated ranges certifies synthetic silence as original audio,
which is worse than no hash at all. Surface the transcript and audio hashes in the export metadata
block beside the existing imported-audio line (`DocxRenderer.cs:64-67`).

**This does not conflict with the standing export ruling.**
`docs/superpowers/specs/2026-08-04-transcript-export-scope-dialog-design.md:78` permanently rules out
hashing recorded-session audio **at export time**, correctly, because hashing a multi-GB FLAC on every
export is unacceptable. Hashing once at **finalize** and carrying the value forward does not re-open
that question. The ruling stands and this feature survives it.

T1-8 detail: extend `ExportProvenance` with session id, export timestamp, app version and
`WeightsFile`; add a metadata line counting human corrections, manual speaker assignments, splits and
**auto-suppressed duplicate segments**. Behind an `ExportOptions` toggle defaulted on, mark corrected
turns per-turn. The user problem is specific: a `.docx` served on the other side currently looks fully
machine-generated but contains rewritten lines and omits suppressed ones, and when that emerges in
cross-examination the omission reads as concealment.

### Plan D - Reachability and shipping (`2026-08-05-tier1d-reachability.md`)

| Item | Deliverable | Effort |
|---|---|---|
| T1-5 | Assign `Application.MainWindow` / route `Owner` through `WindowRegistry`; dialog-local InfoBars; severity on `IUiErrorReporter.Info`; persistent InfoBar on `LiveViewWindow`; export progress + a real CTS | M |
| T1-9 | Selectable read-view text + "Copy with citation" | M |
| T1-10 | `build.ps1`, Velopack packaging + signing, GitHub Actions CI, in-app component acquisition | L |

T1-5 note: the remedy is already in the codebase, unreused. `ReadViewWindow` owns a working
dialog-local InfoBar (`ReadViewWindow.xaml:31-36`) that its own inline edit path uses, while the two
dialogs it parents still route to the shell reporter (`CorrectTextViewModel.cs:57-59`) — making those
two an S-effort subset. `SplitSpeakersWindow.xaml:87-100` is the proven pattern to copy. All four
export calls currently pass `CancellationToken.None` (`ExportDialogViewModel.cs:185-194`).

T1-9 note: "Copy with citation" emits from values `MetadataFormat`/`ExportProvenance` already compose,
e.g. `"..." - J. Smith, 00:41:12, R v Smith call of 2026-07-14 (transcript v2)`.

T1-10 constraint, non-negotiable: **the zero-network property must stay provable by grep.** A grep for
`System.Net|HttpClient|Socket|WebRequest|Dns` across all eight projects returns zero matches today,
and that mechanical checkability is the product's most valuable privacy asset. The component
downloader and any future updater therefore go in **separate helper executables spawned on explicit
user action**, following the existing stdio-child pattern (`CompositionRoot.cs:116-138`), so the grep
over `LocalScribe.App` and `LocalScribe.Core` stays at zero. An in-process `HttpClient` is rejected
regardless of convenience.

## Out of scope for this round

Rejected deliberately, recorded here so they are not re-proposed:

- **Raising the live model ceiling.** Ruled on above.
- **Bulk delete on the sessions grid.** Bulk archive, bulk tag and bulk export are reversible or
  additive; bulk delete is the one operation that can destroy evidence at scale. The per-session
  confirmation plus Recycle-Bin-only deletion (`SessionDeleter.cs:4-8`) exists precisely to make each
  destruction deliberate. Extended selection may ship (Tier 2); delete stays single-select.
- **Redaction or deletion of content in the master record.** Non-negotiable. A *redacted disclosure
  copy* is a derived artefact and a genuine Tier 2 gap; it is not the same thing and must not be
  conflated with it.
- **Automatic audio deletion or retention expiry.** Permanent keep stays.
- **Cloud sync, cloud ASR, telemetry, in-process auto-update.** See the T1-10 constraint.
- **Flipping `Focusable="True"` on the compact pill.** The no-activate property deliberately protects
  the primary use case. Keyboard access comes from window-scoped `InputBindings` mirroring the
  existing Ctrl+Shift+M pattern, in Tier 2's accessibility pass.
- **Optimising `TranscriptProjection`.** Five separate concerns want to change this one path. The row
  contract must be settled before the optimisation. Tier 2.
- **Character-level selection inside a read-view turn.** RECORDED IN EXECUTION (2026-08-06, Plan D
  Task 8): T1-9 shipped row-granular copy - `SelectionMode="Extended"` plus "Copy text" and "Copy
  with citation" over whole turns. A solicitor can copy a turn attributably but still cannot select
  a phrase *inside* one. This is a deliberate, measured scope reduction, not an oversight, and the
  three alternatives were each rejected on a concrete ground: a `FlowDocumentScrollViewer`
  paginates its whole content and so forbids the virtualisation a thousand-row call depends on; a
  per-row `RichTextBox` is affordable under recycling but DESTROYS `SegmentText`, the attached
  behaviour that owns `TextBlock.Inlines` and provides the shipped per-segment tooltip,
  double-click seek and now-playing tint; and the `TextEditorWrapper` reflection trick binds the
  product to a private WPF type. Selection granularity is the TURN, which is also the unit a
  citation attributes, so the two features agree. Revisit in Tier 2 only alongside the row-contract
  work above - both want the same code path.

## Verification

Every plan ends with the smoke items the review's section 7 assigned to it; a static suite cannot
settle any of them. The two that matter most, both Plan C: record a real Webex call and check which
model `session.json` says it used, then import the same audio and read both transcripts side by side
to judge whether the divergence is material to a solicitor.

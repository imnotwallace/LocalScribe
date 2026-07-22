# Import progress bar + ETA + live transcript preview — design (2026-07-22)

## Context

Importing a long audio file (a real 28-minute call surfaced this) shows only a bare
"Transcribing…" label. `AudioImporter`/`OfflinePipelineRunner` report **stage** transitions
(`Copy → Decode → Transcribe → Save`) via `IProgress<ImportStage>` and nothing finer, so the
user has no sense of how long transcription will take and cancels out of uncertainty. The
cancel path itself was also crashing (fixed separately: `WhisperNetEngine.DisposeAsync` now
awaits `_processor.DisposeAsync()`); this design is about the *positive* feedback that makes a
long import feel bounded.

Two improvements were chosen (heads-up-for-long-files and background-import were explicitly
declined this round):

1. **Determinate progress bar + ETA** during the Transcribe stage.
2. **Live transcript preview** — the text scrolling in as it is produced.

Ships on the same app restart as the cancel-crash fix and the compact-pill icon fix.

## Approach

Reuse the per-segment signal the pipeline already emits. `OfflinePipelineRunner` raises
`TranscriptionWorker.SegmentTranscribed` for every segment; its writer loop appends each to the
`TranscriptMerger` and gets back a line carrying `EndMs`, `Source`, and `Text`. That is exactly
the data a progress bar and a preview need, so surface it as a progress stream.

Rejected alternative: have the dialog tail the `transcript.jsonl` file as it is written — racy
against the writer, requires re-parsing, and duplicates state the runner already holds.

## Progress math (the one subtlety: split-stereo)

A split-stereo import maps the source into **two legs** (Local, Remote) and the runner
transcribes them **sequentially** — all of Local `0 → duration`, then all of Remote
`0 → duration`. A naive `endMs / duration` bar would therefore fill to 100 % and reset.

Track **cumulative work** instead:

- `transcribedMs = Σ (max EndMs seen per Source)` — sum of the furthest point reached in each
  leg. Monotonic non-decreasing across the whole import for both mono (1 leg) and split (2 legs).
- `totalWorkMs = legCount × decodedDurationMs`, where `legCount = sources.Count` and
  `decodedDurationMs` is the decoded-stream truth already known to `AudioImporter`.
- `fraction = clamp(transcribedMs / totalWorkMs, 0, 1)`.

Mono/downmix is the simple single-leg case (`totalWorkMs == decodedDurationMs`).

## Types and layers (all additive; existing callers/tests compile unchanged)

```csharp
// LocalScribe.Core.Pipeline
public sealed record TranscriptionProgress(
    long TranscribedMs,          // cumulative across legs
    long TotalMs,                // legCount × decoded duration; 0 = unknown → consumer hides the bar
    string SegmentText,          // the just-appended line's text (for the preview)
    TranscriptSource Source);    // Local/Remote (preview tag when 2 legs)
```

| Layer | Change |
|---|---|
| `OfflineRunOptions` | add `long TotalDurationMs { get; init; }` (per-leg decoded duration; `0` = unknown, e.g. the Stage-2 path — progress simply not reported) |
| `OfflinePipelineRunner.RunAsync` | add optional trailing `IProgress<TranscriptionProgress>? progress = null`. In the writer loop, after `AppendSegmentAsync`, update a `Dictionary<TranscriptSource,long>` of per-source max `EndMs`, and if `progress is not null && TotalDurationMs > 0` report `TranscriptionProgress(Σmax, legCount × TotalDurationMs, line.Text, line.Source)`. Markers do not report. |
| `AudioImporter.ImportAsync` | add optional trailing `IProgress<TranscriptionProgress>? transcriptProgress = null`; set `OfflineRunOptions.TotalDurationMs = decoded.DurationMs`; forward the sink into `runner.RunAsync(options, ct, transcriptProgress)` |
| `ImportRunner` delegate + `ImportDialogViewModel` | delegate gains the `IProgress<TranscriptionProgress>` channel; the window layer's method-group binding to `AudioImporter.ImportAsync` still matches. VM adds a second dispatch-marshalled progress sink. |
| `ImportDialog.xaml` (+ `.xaml.cs`) | determinate `ProgressBar` + progress text + scrolling preview, shown while transcribing; code-behind auto-scrolls the preview to newest on `PreviewLines` change |

### ViewModel state and formatting

```csharp
[ObservableProperty] private double _transcribeProgress;       // 0..1, ProgressBar Maximum=1
[ObservableProperty] private string _transcribeProgressText;   // "42% · ~2 min left"
[ObservableProperty] private bool   _isTranscribing;           // bar+preview visible only during Transcribe
public ObservableCollection<string> PreviewLines { get; } = new();   // last N (=10), source-tagged
```

- `IsTranscribing` is set true when the **stage** sink reports `ImportStage.Transcribe`, and
  false on `Save`/completion/cancel. The elapsed clock for the ETA starts at that transition,
  read via the injected `TimeProvider` (`GetTimestamp`/`GetElapsedTime`) so it is unit-testable.
- **Progress text is percentage + ETA**, deliberately *not* "6:12 of 28:00": with two legs the
  work total is `2 × duration`, so a minutes-of-minutes readout would show a confusing doubled
  duration. `"{fraction:P0} · ~{eta} left"`; the ETA (`elapsed × (1 − f) / f`) appears only once
  `f > 0.03` so it is not wildly jumpy at the very start. Below that threshold: `"{fraction:P0}"`.
- **Preview** appends `SegmentText` (prefixed with a short source tag only when the import has two
  legs), trimmed to the last 10 lines; the view auto-scrolls to the newest. It is a reassurance
  view, not the editor — no editing, no dedup/correction semantics.

## Testing (deterministic, no ggml models)

- `OfflinePipelineRunnerTests` already drives a `FakeTranscriptionEngine`. Add:
  - single-leg: progress reports are monotonic non-decreasing and the final `TranscribedMs`
    equals the total; `TotalMs == decoded duration`.
  - two-leg (Local + Remote WAVs): cumulative `TranscribedMs` never resets when the runner
    switches legs and finishes at `2 × duration`; `TotalMs == 2 × duration`.
  - `TotalDurationMs == 0` (or no `IProgress` passed) reports nothing — existing tests unaffected.
- `ImportDialogViewModel` tests pump `TranscriptionProgress` with a fake `TimeProvider`:
  fraction maps to `TranscribeProgress`; text shows bare `%` before the 3 % ETA threshold and
  `% · ~N left` after; `PreviewLines` accumulates and caps at 10; `IsTranscribing` toggles with
  the stage sink.

## Out of scope (declined this round)

- Heads-up note for long files before starting.
- Running the import in the background instead of a modal dialog.

Both remain easy follow-ons: the duration is already known at file-pick, and the progress plumbing
here is exactly what a background/task-tray surface would consume.

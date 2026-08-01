# Read-view per-segment navigation & timestamps — design

Date: 2026-08-01
Status: approved (design), pending implementation plan
Branch: `feat/smoke-followups-2026-07-31`
Origin: 2026-07-30 smoke round, ITEM 5 ("increasing timestamp delay on long audio")

## Motivation

A user reported that on a long (21:44) imported interview the transcript timestamps
appear to drift increasingly ahead of the audio. A 5-boundary investigation
(VAD → Whisper → offset → storage → read-view → player) proved there is **no drift in the
stored data**:

- `local.flac` is 16 kHz / mono / 20,874,240 samples → 1,304,640 ms, equal to the last
  transcript `endMs` to the millisecond; the v2 re-transcription reads that same file with no
  resampler, and the player plays it and reports position from its own clock. So
  `transcript_ms == flac_sample / 16` by construction.
- Whisper's own timestamps are discarded; every line's `[startMs,endMs]` comes from the VAD
  (`VadCore.Emit`, exact 32 ms/window, silence in place, no accumulation). v1 == v2 byte-identical.

The perceived "drift" is a **display** problem: `SectionGrouper` merges consecutive same-speaker
segments into one paragraph labelled with **only the first segment's** start time (gap threshold
5000 ms ≫ the ~350–990 ms real gaps), and read mode shows **no per-line timestamps**. So a
`[02:10]` block actually spans 02:10→02:20+, every later sentence sits further past the single
printed label (reads as "growing delay"), and a phrase that occurs twice (e.g. "…not giving you
my keys" at 02:03 and 02:18) is impossible to disambiguate against the audio.

## Goals

Make each constituent segment of a merged speaker turn individually addressable in **read mode**,
without breaking the flowing-prose reading experience the user chose to keep:

1. **See** — hovering a segment shows its own `[mm:ss]`.
2. **Navigate** — double-clicking a segment seeks + plays from *that* segment's start (not the
   block start).
3. **Track** — the now-playing highlight follows the exact segment under the playhead.

## Non-goals

- No change to stored timestamps, transcript.jsonl, the projection output (`DisplayRow`), or the
  file renderers. The evidentiary projection invariant holds — Core stays untouched.
- Not fixing Whisper/VAD timestamp *precision* (word-level times, MaxSegmentMs, model choice) —
  logged as a separate latent item; large-v3-turbo is a red herring for timestamps here.
- No republish of the Diarizer (ITEM 3, held) and no diarisation-quality work (ITEM 4, deferred).

## Interaction model

Extends the existing double-click-to-seek gesture (`ReadViewWindow.xaml:316` `OnRowActivated` →
`ReadViewViewModel.JumpToSection`, which seeks the block start + plays). Read text is not
selectable; single-click still selects the row for its context menu.

- Hover a segment → `ToolTip` = `[mm:ss]`, `Cursor = Hand`.
- Double-click a segment → `SeekSegment(seg.StartMs)` (mirrors `JumpToSection`: `Playback.Seek` +
  play). Double-click elsewhere on the row still falls back to block start.
- Playhead within `[seg.StartMs, seg.EndMs)` → that segment tinted.

## Components

Core: **unchanged**. `DisplayRow.Segments` already carries each `RowSegment`
(`Seq, Source, StartMs, EndMs, ProjectedText, RawText, IsCorrected, IsPinned, IsSplitChild,
PartIndex`).

App (`LocalScribe.App`):

- **`ReadSegment : ObservableObject`** (new) — wraps one `RowSegment`; adds
  `[ObservableProperty] bool IsNowPlaying`; exposes `StartMs`, `EndMs`, `Text` (=`ProjectedText`),
  `IsSplitChild`. Mirrors `ReadRow.IsNowPlaying`'s decoupled-from-selection highlight pattern.
- **`ReadRow.Segments`** (new) — `Data.Segments` projected to `IReadOnlyList<ReadSegment>`;
  empty for markers / payload-less rows. Built once per (re)load, never mutated in place.
- **`ReadViewViewModel`**:
  - `SegmentAt(int rowIndex, long positionMs)` — the segment index whose `[StartMs,EndMs)`
    contains the position (greatest-match-wins at boundaries, mirrors `SectionAt`).
  - `TickPlayback` — after `PlayingSectionIndex`, resolve the playing segment, set its
    `IsNowPlaying`, clear the previously-playing one (tracked as a `(rowIndex, segIndex)` cursor).
  - `SeekSegmentCommand(long startMs)` — `Playback.Seek(startMs)` + play if paused.
- **`SegmentText` attached behavior** (new, thin view mechanics) on the paragraph `TextBlock`:
  - `Segments` (bound to `ReadRow.Segments`) and `SeekCommand` (bound to `SeekSegmentCommand`).
  - Builds one `Run` per segment (`ProjectedText` + trailing space); sets `Run.ToolTip` =
    formatted stamp (`~[mm:ss] (estimated)` when `IsSplitChild`, else `[mm:ss]`), `Cursor = Hand`;
    a `MouseLeftButtonDown` handler that fires `SeekCommand` on `e.ClickCount == 2`.
  - Keeps a seq→`Run` map and subscribes to each `ReadSegment.PropertyChanged` so a ~150 ms tick
    only flips one `Run.Background` (no inline rebuild per tick).
  - On container recycling / `Segments` change: unsubscribe, tear down, rebuild. (The `ReadRow`
    recycling hazard is explicit in its doc-comment — guard it.)
  - Empty `Segments` → render `Data.Text` as one plain, non-interactive `Run` (today's behavior).
  - Rejected alternative: `ItemsControl`+`WrapPanel` of clickable `TextBlock`s — wraps at segment
    boundaries, breaking prose flow. Inlines preserve true prose wrapping.

Now-playing tint moves from the row (`ReadViewWindow.xaml:363-369`) to the segment; the row index
is retained only for scroll-into-view. (Whole-turn faint tint + stronger segment tint is a trivial
variant if wanted later.)

## Testing (TDD)

VM-level (headless, deterministic — the 824 App.Tests + 102 Assistant tests stay green):

- `SegmentAt`: correct segment across mid-segment, inter-segment gap, exact boundary, first/last,
  and out-of-range positions.
- `TickPlayback`: `IsNowPlaying` hands off segment→segment within a turn and clears when the
  playing row changes; only one segment playing at a time.
- `SeekSegment`: seeks to the given ms and starts playback when paused; no-ops sensibly otherwise.
- Rows with empty `Segments` (markers, live rows) don't throw and expose no interactive segments.
- `ReadRow.Segments` maps `DisplayRow.Segments` 1:1 in order; markers → empty.

The `SegmentText` behavior is thin view glue verified by runtime smoke (hover time, double-click
seek, playing-segment tint on the 21:44 test session), which the user drives.

## Rollout

Single commit on `feat/smoke-followups-2026-07-31` (ITEM 5), separate from the ITEM 1 assistant-OOM
work. No migration, no schema change, no republish.

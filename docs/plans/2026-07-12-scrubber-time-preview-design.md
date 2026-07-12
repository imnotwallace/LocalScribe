# Playback Scrubber — Live Time Preview — Design (2026-07-12)

## Problem

Dragging the read-view playback seek slider does not feel like a scrubber: the thumb
moves but the position timer (the `00:01` label) stays frozen until the drag is released,
so it is hard to land on a specific time.

## Root cause

To avoid thrashing the (expensive Windows Media Foundation, FLAC) audio seek on every drag
delta, the transport defers the actual seek to drag-release:

- `ReadViewWindow.OnSeekDragStarted` sets `Playback.IsScrubbing = true`
  (`ReadViewWindow.xaml.cs:293-294`); `OnSeekDragCompleted` calls `Seek(SliderValueMs)` then
  clears it (`:296-300`).
- `PlaybackViewModel.OnSliderValueMsChanged` (`PlaybackViewModel.cs:87-91`) **returns early**
  while `IsScrubbing` (or `_syncingSlider`), so it neither seeks nor updates any display.
- The time label binds to `Playback.PositionDisplay` (`ReadViewWindow.xaml:159`), which is
  only written by `Seek` / `Tick` / `MediaEnded` — none of which run during a drag. The
  slider itself binds `Value` TwoWay to `SliderValueMs` (`:165-170`), so the **thumb** moves
  but `PositionDisplay` does not follow it.

Net: during a drag the thumb tracks the pointer but the timer shows the pre-drag position.

## Fix

While scrubbing, make the time label follow the thumb — a visual preview — without moving
the audio. In `PlaybackViewModel.OnSliderValueMsChanged`:

```csharp
partial void OnSliderValueMsChanged(long value)
{
    if (_syncingSlider) return;
    if (IsScrubbing)
    {
        // Live preview: the position label follows the thumb during a drag, but the actual
        // (expensive MF-FLAC) audio seek is still deferred to DragCompleted, so we do not
        // thrash SeekMs on every delta. PositionMs (the true audio position) is left untouched
        // until the release commits via Seek().
        long preview = DurationMs > 0 ? Math.Clamp(value, 0, DurationMs) : value;
        PositionDisplay = Format(preview);
        return;
    }
    Seek(value);
}
```

- `PositionDisplay` now tracks the thumb during the drag; `Format` and the duration clamp
  match `Seek`/`Tick` exactly, so the previewed time reads identically to where the release
  will land.
- `PositionMs` (the real audio clock) and `_player.SeekMs` are deliberately **not** touched
  during the drag — the single audio seek still happens once, in `OnSeekDragCompleted → Seek`
  (unchanged). This is the "time label follows; audio seeks on release" behavior chosen at
  spec review (avoids janky repeated FLAC seeks).
- `Tick` remains suppressed during scrubbing (`:155`), so the ~150 ms poll cannot fight the
  preview; on release, `Seek` sets `PositionMs`/`PositionDisplay`/`SliderValueMs` coherently
  and normal `Tick` polling resumes.

Track-click and arrow/Page/Home/End keys are unaffected: they never raise
`Thumb.DragStarted/Completed`, so `IsScrubbing` stays false and they still commit immediately
through the `Seek(value)` branch (`ReadViewWindow.xaml.cs:286-292`).

## Testing

`PlaybackViewModelTests` (the VM is WPF-free; tests drive it directly):

- **Preview during scrub:** with a loaded media (`DurationMs > 0`), set `IsScrubbing = true`,
  then assign `SliderValueMs` to a new value → assert `PositionDisplay == Format(value)` and
  that the audio did **not** move (`PositionMs` unchanged; the fake `IDualAudioPlayer` recorded
  no `SeekMs`).
- **Commit on release:** then call `Seek(value)` (what `DragCompleted` does) and set
  `IsScrubbing = false` → assert `PositionMs`, `PositionDisplay`, and one `SeekMs(value)`.
- **Clamp:** a scrub value beyond `DurationMs` previews `Format(DurationMs)`.
- **Non-scrub path unchanged:** with `IsScrubbing == false`, assigning `SliderValueMs` still
  seeks immediately (existing behavior; regression guard).

## Out of scope

- Live audio seeking while dragging (rejected at review — MF-FLAC seek is too slow to do per
  delta without stutter).
- Previewing the transcript "now playing" section highlight during a drag (the timer preview
  alone addresses the reported issue; the highlight still updates from `PositionMs` after the
  release, via `Tick`).

# Playback Scrubber Live Time Preview Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** While the user drags the read-view seek slider, the position time label follows the thumb as a live preview, while the actual (expensive Windows Media Foundation FLAC) audio seek stays deferred to drag-release.
**Architecture:** The entire change lives in the WPF-free `PlaybackViewModel.OnSliderValueMsChanged` partial handler: when `IsScrubbing`, it updates `PositionDisplay` (clamped to `DurationMs`, formatted identically to `Seek`/`Tick`) and returns without touching `PositionMs` or the player; the single `SeekMs` still fires on release via the window's existing `OnSeekDragCompleted -> Seek`. No XAML change is needed — the label already binds `Playback.PositionDisplay` and the slider already binds `Value` TwoWay to `SliderValueMs`.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints
- Target branch: `fix/scrubber-time-preview` (the design spec `docs/plans/2026-07-12-scrubber-time-preview-design.md` is already committed there @ 562e15b; it is the only diff vs master).
- 0-warning build gate must hold.
- Tests: xUnit. Run a filtered test with: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~PlaybackViewModelTests" --nologo`
- IMPORTANT: the LocalScribe app may be running and LOCK its bin DLL/exe (MSB3027 copy error - NOT a compile error). When that happens, build/test to an isolated output so the lock is avoided: append `-p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` to the dotnet test command. Never kill the user's app.
- Never use Unicode emojis in test code or scripts (project rule).
- Out of scope (rejected at spec review; do NOT implement): live audio seeking on every drag delta (MF-FLAC seek too slow), and previewing the transcript "now playing" section highlight during a drag (the highlight still updates from `PositionMs` after release, via `Tick`).
- Commit messages follow the repo style: fix(app)/feat(app)/test(app)/docs(...). Every commit message MUST end with these two trailer lines EXACTLY:
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
```

---

### Task 1: Live time-label preview while scrubbing (`PlaybackViewModel`)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/PlaybackViewModel.cs` (the `OnSliderValueMsChanged` handler + its XML doc, currently lines 81-91)
- Test: `tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs` (add three `[Fact]`s; the existing `FakePlayer`, `MakeVm()`, `WriteAudio(...)` and private `Format(...)` helpers are reused unchanged)

**Interfaces:**
- Consumes (already exist on `PlaybackViewModel`): `bool IsScrubbing` (`ObservableProperty`), `long SliderValueMs` (`ObservableProperty`, TwoWay-bound), `long DurationMs`, `long PositionMs`, `string PositionDisplay`, `void Seek(long ms)`, `private bool _syncingSlider`, `private static string Format(long ms)`. `System.Math.Clamp` is already used in this file (`Seek`, line 166).
- Consumes (test double, already in the test file): `FakePlayer : IDualAudioPlayer` exposing `long DurationMs`, `long PositionMs`, `List<string> Calls`, `RaiseReady()`, and recording `SeekMs` as `"Seek:{ms}"`.
- Produces: no new public members. Behavior change only — the `IsScrubbing` branch of `OnSliderValueMsChanged` now writes `PositionDisplay` (previewed, clamped) and returns; `PositionMs`/`_player.SeekMs` remain untouched during a drag.

**Steps:**

- [ ] Add the three new `[Fact]` tests to `tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs` (place them next to the other slider tests, e.g. after `Seek_updates_slider_value` at line 394). SHOW — the actual test code:

```csharp
    [Fact]
    public void Scrub_preview_moves_the_time_label_but_not_the_audio()
    {
        // Design 2026-07-12: while IsScrubbing, a SliderValueMs change previews the position
        // label (follows the thumb) but must NOT move the real audio clock or issue a SeekMs -
        // the single audio seek is deferred to DragCompleted -> Seek() on release.
        WriteAudio("s-preview", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-preview", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 60_000;
        _player.RaiseReady();

        vm.IsScrubbing = true;
        _player.Calls.Clear();                            // ignore the Load recorded by Resolve

        vm.SliderValueMs = 42_000;                        // drag delta arrives via the TwoWay binding

        Assert.Equal(Format(42_000), vm.PositionDisplay); // label follows the thumb
        Assert.Equal(0, vm.PositionMs);                   // real audio clock untouched
        Assert.DoesNotContain(_player.Calls, c => c.StartsWith("Seek:"));   // no MF seek per delta
    }

    [Fact]
    public void Scrub_preview_clamps_a_value_beyond_duration_to_duration()
    {
        // A drag can overshoot Maximum transiently; the preview clamps to DurationMs so the label
        // reads identically to where the release will land (Seek clamps the same way).
        WriteAudio("s-preview-clamp", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-preview-clamp", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        vm.IsScrubbing = true;
        vm.SliderValueMs = 30_000;                        // beyond the media

        Assert.Equal(Format(23_000), vm.PositionDisplay); // "00:23", clamped to duration
        Assert.Equal(0, vm.PositionMs);                   // still no audio move
    }

    [Fact]
    public void Scrub_release_commits_a_single_seek_at_the_previewed_value()
    {
        // DragCompleted calls Seek(SliderValueMs) then clears IsScrubbing (ReadViewWindow
        // OnSeekDragCompleted, xaml.cs:296-300). After a preview drag the release must set
        // PositionMs/PositionDisplay coherently and issue exactly one SeekMs at the shown value.
        WriteAudio("s-commit", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-commit", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 60_000;
        _player.RaiseReady();

        vm.IsScrubbing = true;
        vm.SliderValueMs = 42_000;                        // preview only
        _player.Calls.Clear();

        vm.Seek(vm.SliderValueMs);                         // ...what DragCompleted does...
        vm.IsScrubbing = false;                            // ...then clears the guard

        Assert.Equal(42_000, vm.PositionMs);
        Assert.Equal(Format(42_000), vm.PositionDisplay);
        Assert.Equal(1, _player.Calls.Count(c => c == "Seek:42000"));   // one seek, on release
    }
```

- [ ] Run the new tests and see them FAIL (the current handler returns early on `IsScrubbing` without writing `PositionDisplay`, so it stays at the initial `"00:00"`):

  `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~PlaybackViewModelTests" --nologo`

  Expected: `Scrub_preview_moves_the_time_label_but_not_the_audio` and `Scrub_preview_clamps_a_value_beyond_duration_to_duration` FAIL with `Assert.Equal() Failure ... Expected: 00:42 (resp. 00:23) / Actual: 00:00`. `Scrub_release_commits_a_single_seek_at_the_previewed_value` already PASSES (the release path is unchanged) — it is the coherent-commit regression guard. (If the app is running and the run reports MSB3027, re-run with the `-p:BaseOutputPath=...\isobin\` suffix from Global Constraints.)

- [ ] Implement the MINIMAL real change in `src/LocalScribe.App/ViewModels/PlaybackViewModel.cs`. Replace the doc comment + handler (currently lines 81-91) — SHOW old and new:

  OLD:
```csharp
    /// <summary>Fires for every WPF-side Value change on the TwoWay-bound slider - track click,
    /// arrow/Page/Home/End keys, and thumb-drag deltas - regardless of whether Slider's own class
    /// handlers marked the routed event Handled (they run before our instance handlers and always
    /// do for track-click/keyboard). Commits immediately unless this is the VM's own echo
    /// (<see cref="_syncingSlider"/>) or the user is mid-drag (<see cref="IsScrubbing"/>, released
    /// via the window's DragCompleted handler instead).</summary>
    partial void OnSliderValueMsChanged(long value)
    {
        if (_syncingSlider || IsScrubbing) return;
        Seek(value);
    }
```

  NEW:
```csharp
    /// <summary>Fires for every WPF-side Value change on the TwoWay-bound slider - track click,
    /// arrow/Page/Home/End keys, and thumb-drag deltas - regardless of whether Slider's own class
    /// handlers marked the routed event Handled (they run before our instance handlers and always
    /// do for track-click/keyboard). Commits the seek immediately unless this is the VM's own echo
    /// (<see cref="_syncingSlider"/>), which returns; or the user is mid-drag
    /// (<see cref="IsScrubbing"/>), where the position label previews the thumb but the audio seek
    /// is deferred to the window's DragCompleted handler (design 2026-07-12).</summary>
    partial void OnSliderValueMsChanged(long value)
    {
        if (_syncingSlider) return;
        if (IsScrubbing)
        {
            // Live preview: the position label follows the thumb during a drag, but the actual
            // (expensive MF-FLAC) audio seek is still deferred to DragCompleted, so we do not
            // thrash SeekMs on every delta. PositionMs (the true audio position) is left untouched
            // until the release commits via Seek(). The clamp + Format match Seek/Tick exactly, so
            // the previewed time reads identically to where the release will land.
            long preview = DurationMs > 0 ? Math.Clamp(value, 0, DurationMs) : value;
            PositionDisplay = Format(preview);
            return;
        }
        Seek(value);
    }
```

- [ ] Run the full `PlaybackViewModelTests` class and see ALL PASS (the two new preview tests now pass; the commit guard still passes; and the existing regression guards `SliderValue_change_commits_seek_when_not_scrubbing` (line 328) and `SliderValue_change_during_scrub_does_not_seek` (line 348) — the non-scrub-path-unchanged coverage from the spec's Testing section — remain green):

  `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~PlaybackViewModelTests" --nologo`

  Expected: `Passed!` with 0 failures.

- [ ] Commit:

```
git add src/LocalScribe.App/ViewModels/PlaybackViewModel.cs tests/LocalScribe.App.Tests/PlaybackViewModelTests.cs
git commit -m "fix(app): live time-label preview while scrubbing the seek slider

OnSliderValueMsChanged now previews PositionDisplay (clamped to DurationMs,
formatted like Seek/Tick) while IsScrubbing, without touching PositionMs or
issuing a SeekMs. The single MF-FLAC audio seek stays deferred to
DragCompleted -> Seek() on release. +3 VM tests (preview, clamp, commit).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 2: Verify end-to-end (real drag gesture) + full-suite / 0-warning gate

**Files:**
- Test (automated regression): `tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj` (whole suite, including `tests/LocalScribe.App.Tests/XamlHygieneTests.cs` — no XAML changed, so this must stay green)
- Modify: none (verification only)

**Interfaces:** Consumes the window wiring already present in `src/LocalScribe.App/ReadViewWindow.xaml` (SeekSlider `Value="{Binding Playback.SliderValueMs, Mode=TwoWay}"`, label `Text="{Binding Playback.PositionDisplay}"`, `Thumb.DragStarted="OnSeekDragStarted"`, `Thumb.DragCompleted="OnSeekDragCompleted"`, lines 159-170) and `src/LocalScribe.App/ReadViewWindow.xaml.cs` (`OnSeekDragStarted` sets `IsScrubbing = true` at 293-294; `OnSeekDragCompleted` calls `Seek(SliderValueMs)` then clears it at 296-300). Produces: no code — only a verified acceptance record.

**Rationale:** `Thumb.DragStarted/DragCompleted` fire only on a real pointer drag, so the "label follows the thumb during the drag, audio seeks once on release" behavior cannot be unit-tested; it needs a manual smoke in the running WPF app. This task also guards that nothing else regressed.

**Steps:**

- [ ] Run the whole App test suite (must be green; XamlHygieneTests included) and confirm a 0-warning build:

  `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo`

  Expected: `Passed!`, 0 failed, and no build warnings in the output. (If the user's app holds the lock -> MSB3027, append the `-p:BaseOutputPath=...\isobin\` suffix and re-run; a copy lock is not a compile/test failure.)

- [ ] Manual smoke in the running app (WPF; the reviewer performs this — do NOT kill the user's app to launch your own; ask the user to run it or use an existing instance). Open a session that has retained audio, open its Read view, and confirm:
  - **Preview follows thumb:** grab the seek-slider thumb and drag slowly left/right. The time label immediately left of the slider (`PositionDisplay`) updates continuously as you drag, tracking the thumb.
  - **Audio does not seek mid-drag:** during the drag there is no audible jump/stutter and the audio position does not move (playback, if playing, continues from the pre-drag point) until you release.
  - **Release commits once:** on releasing the thumb, the audio jumps exactly once to the time shown on the label; playback resumes/continues from there and the ~150 ms poll takes over (label keeps advancing if playing).
  - **Overshoot clamps:** dragging hard to the far right pins the label at the total duration (never past it).
  - **Non-drag gestures unchanged (regression):** a single click on the slider track, and Left/Right/PageUp/PageDown/Home/End keys while the slider is focused, each still seek immediately (label and audio move together on the gesture, no deferral).

- [ ] No commit (verification only). If the smoke reveals a defect, return to Task 1, add a failing VM test that reproduces it where possible, fix, and re-run.

---

## Self-review

**(a) Spec coverage — every spec section maps to a task:**
- Problem + Root cause (label frozen during drag; `OnSliderValueMsChanged` early-returns on `IsScrubbing`; label binds `PositionDisplay`, only written by `Seek`/`Tick`/`MediaEnded`) -> context for Task 1.
- Fix (the `OnSliderValueMsChanged` preview branch: `preview = DurationMs > 0 ? Math.Clamp(value,0,DurationMs) : value; PositionDisplay = Format(preview);` with `PositionMs`/`SeekMs` untouched, seek deferred to `DragCompleted`) -> Task 1 implementation step (verbatim logic).
- Testing bullet 1 (preview during scrub: `PositionDisplay == Format(value)`, `PositionMs` unchanged, no `SeekMs`) -> Task 1 test `Scrub_preview_moves_the_time_label_but_not_the_audio`.
- Testing bullet 2 (commit on release: `Seek(value)` then clear scrubbing -> `PositionMs`, `PositionDisplay`, one `SeekMs(value)`) -> Task 1 test `Scrub_release_commits_a_single_seek_at_the_previewed_value`.
- Testing bullet 3 (clamp beyond `DurationMs` previews `Format(DurationMs)`) -> Task 1 test `Scrub_preview_clamps_a_value_beyond_duration_to_duration`.
- Testing bullet 4 (non-scrub path unchanged / regression guard) -> covered by the pre-existing `SliderValue_change_commits_seek_when_not_scrubbing` (line 328) and `SliderValue_change_during_scrub_does_not_seek` (line 348), asserted green in Task 1's final run and Task 2's full-suite run.
- Track-click / arrow-keys unaffected (never raise `DragStarted/Completed`, `IsScrubbing` stays false, commit immediately) -> Task 2 manual smoke ("Non-drag gestures unchanged").
- Out of scope (no live audio seek per delta; no highlight preview) -> Global Constraints (explicitly excluded; not implemented).

**(b) Placeholder scan:** none. Every code block is real: the new handler is the spec's exact logic grounded in the current file (lines 81-91), and all three tests use only members and helpers that already exist in `PlaybackViewModelTests.cs` (`WriteAudio`, `MakeVm`, `_player`, `Format`, `SourceKind`, `AudioFormat`, xUnit `Assert.Equal`/`DoesNotContain`/`Count`). No "TBD", no "similar to".

**(c) Type consistency across tasks:** the change touches no signatures — `OnSliderValueMsChanged(long value)`, `Seek(long)`, `Format(long)`, `PositionDisplay` (string), `PositionMs`/`SliderValueMs`/`DurationMs` (long), `IsScrubbing` (bool) all pre-exist with the shapes used. `Math.Clamp(long,long,long)` is already used in the same file. `FakePlayer.Calls` records `SeekMs` as `"Seek:{ms}"`, matching the `"Seek:42000"` / `StartsWith("Seek:")` assertions. No new public members are introduced, so no later task depends on anything undefined. Task 2 only reads XAML/handler wiring that already exists.

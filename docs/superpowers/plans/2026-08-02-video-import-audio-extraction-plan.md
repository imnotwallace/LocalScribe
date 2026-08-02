# Video import - audio-only extraction (user addition, 2026-08-02)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox ("- [ ]") syntax for tracking.

> **Provenance note:** this feature is a user addition approved 2026-08-02. It is NOT part of
> `docs/superpowers/specs/2026-08-02-ux-round-design.md` - that spec covers a separate UX round and
> has no bearing on this plan. All file/line anchors below were verified by direct inspection
> against worktree HEAD `8050a61` (2026-08-02) and may drift by execution time; re-read the cited
> lines before editing if the file has moved on.

**Goal:** Let the Import File flow accept common VIDEO containers (MP4, M4V, MOV, MKV, WEBM, AVI,
WMV) in addition to audio files, transcribing them by extracting the audio channel only - "we
simply only extract the audio channel." The original video file's own name/hash remains the
recorded provenance, exactly like an audio import.

**Architecture:** LocalScribe's import pipeline already decodes every non-WAV input through a
generic ffmpeg subprocess (`FfmpegAudioDecoder`) that probes the first audio stream via `ffprobe`
and decodes with `-vn` (drop video) into a native-rate PCM WAV; this path is container-agnostic
by construction, so it already extracts audio-only from a video file with ZERO production changes
(verified below by direct experimentation with the bundled ffmpeg binary against a synthesized
MP4). The only real gap is presentation-layer: the Import dialog's file-picker filter and its
tooltip/title copy still say "audio" only, so a user cannot easily pick a video file today even
though the pipeline underneath would handle it correctly. This plan widens that filter and copy,
and adds a real-ffmpeg regression fixture proving the video path end-to-end (probe, decode,
provenance, transcript) so the already-correct behavior is locked in and never silently regresses.

**Tech Stack:** .NET 10 (`net10.0-windows`, LangVersion latest), CommunityToolkit.Mvvm
(`[ObservableProperty]`/`RelayCommand`) for the WPF-free dialog VM, WPF/Wpf.Ui XAML for the two
view-layer copy edits, NAudio (`WaveFileWriter`/`WaveFileReader`) for fixture WAV synthesis, the
bundled ffmpeg/ffprobe (`tools\ffmpeg`, LGPL SHARED build pinned by `tools\fetch-ffmpeg.ps1`) as a
real subprocess in the one Core fixture test, xUnit.

**Task order:** Tasks 1-4 are independent and may be done in any order. Task 5 (final gate) needs
all of them landed first.

## Global Constraints

- Strict TDD: write the failing test, run it, watch it fail with the expected message, THEN implement - every task, no exceptions.
- No Unicode emojis anywhere in code, tests, comments, or scripts.
- VMs stay WPF-free: nothing under `src\LocalScribe.App\ViewModels` or `src\LocalScribe.Core` may reference WPF types.
- Invariant culture in all new formatting/parsing code (none of this plan's new code parses or formats numbers, but any touched call site must stay invariant).
- Transcripts/audio are evidence: original file provenance (`ImportedSource.FileName`/`Sha256`) is recorded unchanged and never rewritten; nothing in this plan is destructive or deletes/redacts anything.
- Close any running `LocalScribe.App.exe` before building - a running app locks `Core.dll` and the build fails with MSB3027. Check with `tasklist | findstr LocalScribe` and close that specific process only (never kill broadly).
- View-layer-only steps (raw XAML literal text) cannot be unit-tested here (no STA/WPF harness) - such verification is a smoke-runbook checkbox, never a fake test.
- ffmpeg fixture tests (`[Trait("Category","Fixture")]`) require `tools\ffmpeg` present (run `tools\fetch-ffmpeg.ps1` first if it is missing); this worktree already has it.

---

### Task 1: Widen the Import file-picker filter to accept video containers

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs` (the `FileFilter` constant, currently lines 38-39)
- Modify: `tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs` (add one new `[Fact]`, inserted immediately above the existing first test `PickFile_probes_and_defaults_title_and_recorded_date_from_media_tag` at line 81)

**Interfaces:**
- Consumes/Produces (unchanged shape, new value): `public const string ImportDialogViewModel.FileFilter` (`src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs:38-39`) - a standard WPF `OpenFileDialog` filter string, consumed by `PickFileAsync` (`:218`) via `_pickOpenPath(new OpenPathRequest(FileFilter))`. No signature change; only the string literal's content changes.

**Steps:**

- [ ] 1. Write the failing test. In `tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs`, insert this new `[Fact]` directly above line 81 (`[Fact]` / `public async Task PickFile_probes_and_defaults_title_and_recorded_date_from_media_tag()`):

```csharp
    [Fact]
    public void FileFilter_accepts_common_video_containers_alongside_every_existing_audio_extension()
    {
        Assert.StartsWith("Audio and video files", ImportDialogViewModel.FileFilter);
        foreach (string ext in new[] { "*.mp4", "*.m4v", "*.mov", "*.mkv", "*.webm", "*.avi", "*.wmv" })
            Assert.Contains(ext, ImportDialogViewModel.FileFilter);
        // Video support is additive - every original audio extension must still be offered.
        foreach (string ext in new[] { "*.wav", "*.flac", "*.mp3", "*.m4a", "*.aac", "*.wma", "*.ogg" })
            Assert.Contains(ext, ImportDialogViewModel.FileFilter);
        Assert.EndsWith("|All files (*.*)|*.*", ImportDialogViewModel.FileFilter);
    }

```

  Run:
  ```
  dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests.FileFilter_accepts_common_video_containers_alongside_every_existing_audio_extension"
  ```
  Expected RED: fails on `Assert.StartsWith("Audio and video files", ImportDialogViewModel.FileFilter)` - the current constant starts with `"Audio files ("`, not `"Audio and video files ("`.

- [ ] 2. Implement: widen the filter. In `src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs`, replace lines 38-39:

```csharp
    public const string FileFilter =
        "Audio files (*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg)|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg|All files (*.*)|*.*";
```

  with:

```csharp
    public const string FileFilter =
        "Audio and video files (*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg;*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv)|*.wav;*.flac;*.mp3;*.m4a;*.aac;*.wma;*.ogg;*.mp4;*.m4v;*.mov;*.mkv;*.webm;*.avi;*.wmv|All files (*.*)|*.*";
```

  Run the same filter again - expected GREEN:
  ```
  dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests.FileFilter_accepts_common_video_containers_alongside_every_existing_audio_extension"
  ```
  `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

- [ ] 3. Regression-check the whole file (the constant is read by other existing tests too):
  ```
  dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~ImportDialogViewModelTests"
  ```
  Expect every test in the file passing (no prior test asserted the OLD filter text, so none should break).

- [ ] 4. Commit:
```
git add src\LocalScribe.App\ViewModels\ImportDialogViewModel.cs tests\LocalScribe.App.Tests\ImportDialogViewModelTests.cs
git commit -m "feat(import): accept common video containers in the file picker"
```

---

### Task 2: Core fixture test - prove video-container import already extracts audio-only, end-to-end

**Files:**
- Modify: `tests\LocalScribe.Core.Tests\AudioImportFixtureTests.cs` (add one new `[Fact]`, inserted between the end of the existing `RealFfmpeg_imports_a_generated_stereo_mp3_end_to_end` method at line 101 and the class's closing brace at line 102)

**Interfaces:**
- Consumes (existing, unchanged): `AudioImporter.ImportAsync` (`src\LocalScribe.Core\Import\AudioImporter.cs:113-115`), `FfmpegAudioDecoder` (`src\LocalScribe.Core\Import\FfmpegAudioDecoder.cs:13-19` ctor, `:31-39` `DecodeAsync` - the production decode command is `-v error -nostdin -y -i "{path}" -vn -acodec pcm_s16le "{outPath}"`, unchanged by this plan), `FfmpegLocator.FindToolsDir()` (`src\LocalScribe.Core\Import\FfmpegLocator.cs:13`), `ImportRequest`/`StereoMapping.Downmix` (`AudioImporter.cs:20-70`), `SessionStore` (`LocalScribe.Core.Storage`), `FlacPcmReader.ReadMono16k` (`LocalScribe.Core.Diarisation`), the class's own existing `EnergyProbe`/`EchoFactory` nested fakes (`AudioImportFixtureTests.cs:28-40`, reused as-is - no new nested type in this task).
- Produces: no new production types; this is a coverage-only test method.

**Verification note (why this task changes NO production code):** direct experimentation against the bundled `tools\ffmpeg\ffmpeg.exe`/`ffprobe.exe` during planning confirmed that `FfmpegAudioDecoder`'s existing decode command (`-vn -acodec pcm_s16le`, already present, no edit needed) correctly strips the video stream from a synthesized MP4 (one `mpeg4` video stream + one `aac` audio stream) and produces a WAV with the exact source channel count/sample rate/duration; `ffprobe`'s JSON parse in `ParseProbeJson` (`FfmpegAudioDecoder.cs:98-110`) already skips non-audio streams (`codec_type != "audio"`) and reads the first (only) audio stream's `channels`/`sample_rate`/`duration`. Because a WAV container cannot hold a video stream, `-vn` alone is sufficient - no explicit `-map` is needed for the single-audio-stream case this plan targets (multi-audio-track selection is out of scope for v1). This task therefore adds regression coverage over already-correct behavior rather than fixing a defect.

**Steps:**

- [ ] 1. Write the failing test (it does not exist yet, so the filter matches nothing - the RED signal for a coverage-only addition). Run:
  ```
  dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImportFixtureTests.RealFfmpeg_imports_a_generated_mp4_video_end_to_end"
  ```
  Expected RED:
  ```
  No test matches the given testcase filter `FullyQualifiedName~AudioImportFixtureTests.RealFfmpeg_imports_a_generated_mp4_video_end_to_end` in ...\LocalScribe.Core.Tests.dll
  ```

- [ ] 2. Add the test. In `tests\LocalScribe.Core.Tests\AudioImportFixtureTests.cs`, insert this new method between the closing `}` of `RealFfmpeg_imports_a_generated_stereo_mp3_end_to_end` (line 101) and the class's closing `}` (line 102):

```csharp
    [Fact]
    public async Task RealFfmpeg_imports_a_generated_mp4_video_end_to_end()
    {
        string? tools = FfmpegLocator.FindToolsDir();
        if (tools is null)
            throw new FileNotFoundException(
                "FFmpeg missing. Run tools/fetch-ffmpeg.ps1 (two-run pin flow), or set LOCALSCRIBE_FFMPEG.");

        // Generate the source: 200 ms silence + 1500 ms tone + 1000 ms silence, mono 44.1 kHz,
        // then let the REAL ffmpeg mux it against a synthetic black video track (native mpeg4
        // encoder - the BtbN LGPL SHARED build tools/fetch-ffmpeg.ps1 pins has no GPL libx264, so
        // this is the only video encoder available, and it is enough: we never need to PLAY the
        // video, only prove it gets stripped) into a tiny real MP4 with two streams. This is the
        // feature's whole premise ("we simply only extract the audio channel") - proving the
        // EXISTING decode command already excludes the video track with no production change.
        string wav = Path.Combine(_root, "tone.wav");
        using (var w = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)))
        {
            int silence = 8820, speech = 66150, tail = 44100;
            var buf = new float[silence + speech + tail];
            for (int f = 0; f < speech; f++)
                buf[silence + f] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / 44100.0));
            w.WriteSamples(buf, 0, buf.Length);
        }
        string mp4 = Path.Combine(_root, "meeting recording.mp4");
        var encode = Process.Start(new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"),
            $"-v error -nostdin -y -i \"{wav}\" -f lavfi -i \"color=c=black:s=64x64:r=1\" " +
            $"-shortest -c:v mpeg4 -pix_fmt yuv420p -c:a aac -b:a 96k \"{mp4}\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await encode.WaitForExitAsync();
        Assert.Equal(0, encode.ExitCode);

        var paths = new StoragePaths(Path.Combine(_root, "store"));
        var importer = new AudioImporter(paths, new Settings { Language = "en" },
            new FfmpegAudioDecoder(tools), new EchoFactory(), () => new EnergyProbe(),
            new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), TimeProvider.System, "fixture",
            availableModels: () => new HashSet<string> { "tiny.en", "base.en", "small.en" });

        string id = await importer.ImportAsync(new ImportRequest
        {
            SourcePath = mp4, Title = "Fixture video call",
            RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 5, 14, 30, 0))),
            Stereo = StereoMapping.Downmix,
        }, progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("imported", session!.Origin);
        // Provenance is the ORIGINAL video file's own name/hash (design 2026-07-13 section 4) -
        // never renamed, never pointed at the audio-only decode byproduct.
        Assert.Equal("meeting recording.mp4", session.ImportedSource!.FileName);
        Assert.Contains("mp4", session.ImportedSource.ContainerFormat);
        Assert.Equal(1, session.ImportedSource.DecodedChannels);     // audio-only: the video track never reaches here
        Assert.Equal(44100, session.ImportedSource.DecodedSampleRate);
        Assert.Equal("mono", session.ImportedSource.ChannelMapping);
        Assert.InRange(session.ImportedSource.DecodedDurationMs, 2400, 3000);

        float peak = FlacPcmReader.ReadMono16k(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac))
            .Max(MathF.Abs);
        Assert.True(peak > 0.2f, $"peak={peak}");
        Assert.True(File.Exists(paths.TranscriptMd(id)));
        Assert.True(session.SegmentCount >= 1);
    }
```

  Run:
  ```
  dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImportFixtureTests.RealFfmpeg_imports_a_generated_mp4_video_end_to_end"
  ```
  Expected GREEN (no production code changed - the existing decode/probe path already handles it):
  `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

- [ ] 3. Regression-check the whole fixture file:
  ```
  dotnet test tests\LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImportFixtureTests"
  ```
  Expect both fixture tests (the pre-existing MP3 one and the new MP4 one) passing.

- [ ] 4. Commit:
```
git add tests\LocalScribe.Core.Tests\AudioImportFixtureTests.cs
git commit -m "test(import): real-ffmpeg fixture proves video-container audio extraction end-to-end"
```

---

### Task 3: Import-tooltip copy mentions video containers

**Files:**
- Modify: `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs` (the `ImportTooltip` property, currently lines 128-130)
- Modify: `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (add one new `[Fact]`, inserted after the closing `}` of `ImportAudioCommand_raises_only_when_idle_and_available` at line 687 and before the `// Task 2 (UX round 2026-07-18...` comment at line 689)

**Interfaces:**
- Consumes/Produces (unchanged shape, new value): `public string SessionsPageViewModel.ImportTooltip { get; }` (`src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs:128-130`), bound by `ToolTip="{Binding ImportTooltip}"` in `src\LocalScribe.App\Pages\SessionsPage.xaml:53`. Only the "available" branch's string literal changes; the "unavailable" branch (FFmpeg-missing message) is untouched.

**Steps:**

- [ ] 1. Write the failing test. In `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs`, insert this new `[Fact]` after line 687 (the closing `}` of `ImportAudioCommand_raises_only_when_idle_and_available`):

```csharp

    [Fact]
    public void ImportTooltip_mentions_video_containers_when_available()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(),
            new RecordingErrors(), dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { },
            importAvailable: true);

        Assert.Contains("video", vm.ImportTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MP4", vm.ImportTooltip);
        Assert.Contains("WAV", vm.ImportTooltip);            // audio formats still listed
        session.Dispose();
    }
```

  Run:
  ```
  dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests.ImportTooltip_mentions_video_containers_when_available"
  ```
  Expected RED: fails on `Assert.Contains("video", vm.ImportTooltip, StringComparison.OrdinalIgnoreCase)` - the current text is `"Import an audio file (WAV, FLAC, MP3, M4A, WMA, OGG) as a new session"`, which contains no "video"/"MP4".

- [ ] 2. Implement: widen the tooltip copy. In `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs`, replace lines 128-130:

```csharp
    public string ImportTooltip => ImportAvailable
        ? "Import an audio file (WAV, FLAC, MP3, M4A, WMA, OGG) as a new session"
        : "Import is unavailable - FFmpeg was not found. " + LocalScribe.Core.Import.FfmpegLocator.MissingMessage;
```

  with:

```csharp
    public string ImportTooltip => ImportAvailable
        ? "Import an audio or video file (WAV, FLAC, MP3, M4A, WMA, OGG, MP4, MOV, MKV, WEBM, AVI, WMV) as a new session - video is imported audio-only"
        : "Import is unavailable - FFmpeg was not found. " + LocalScribe.Core.Import.FfmpegLocator.MissingMessage;
```

  Run again - expected GREEN:
  ```
  dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests.ImportTooltip_mentions_video_containers_when_available"
  ```
  `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`

- [ ] 3. Regression-check the whole file (the existing `ImportAudioCommand_raises_only_when_idle_and_available` test also reads `ImportTooltip` - confirm it still only asserts `DoesNotContain("fetch-ffmpeg", ...)` / `Contains("fetch-ffmpeg.ps1", ...)` on the two branches, both unaffected):
  ```
  dotnet test tests\LocalScribe.App.Tests --filter "FullyQualifiedName~SessionsPageViewModelTests"
  ```
  Expect every test in the file passing.

- [ ] 4. Commit:
```
git add src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs
git commit -m "feat(import): mention video formats in the Import tooltip"
```

---

### Task 4: View-layer copy - dialog title and action-bar button text

**Files:**
- Modify: `src\LocalScribe.App\ImportDialog.xaml` (the `Title` attribute, currently line 5)
- Modify: `src\LocalScribe.App\Pages\SessionsPage.xaml` (the Import button's `Content`, currently line 51)

**Interfaces:** none - these are raw XAML string literals with no VM binding, so nothing here is
programmatically consumed/produced by other code. No unit test is possible for WPF XAML literal
text in this repo (no STA/WPF test harness); verification is the smoke-runbook checkbox appended
in Task 5.

**Steps:**

- [ ] 1. In `src\LocalScribe.App\ImportDialog.xaml`, change line 5 from:
```xml
<Window x:Class="LocalScribe.App.ImportDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        Title="Import audio" Width="480" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize">
```
  to:
```xml
<Window x:Class="LocalScribe.App.ImportDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        Title="Import audio or video" Width="480" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize">
```

- [ ] 2. In `src\LocalScribe.App\Pages\SessionsPage.xaml`, change line 51 from:
```xml
            <ui:Button Content="Import audio..." Appearance="Secondary" Margin="0,0,8,8"
                       IsEnabled="{Binding ImportAvailable}"
                       ToolTip="{Binding ImportTooltip}"
                       ToolTipService.ShowOnDisabled="True"
                       Command="{Binding ImportAudioCommand}" />
```
  to:
```xml
            <ui:Button Content="Import audio or video..." Appearance="Secondary" Margin="0,0,8,8"
                       IsEnabled="{Binding ImportAvailable}"
                       ToolTip="{Binding ImportTooltip}"
                       ToolTipService.ShowOnDisabled="True"
                       Command="{Binding ImportAudioCommand}" />
```

- [ ] 3. Build the App project to confirm the XAML still compiles (BAML regeneration catches typos a text edit alone cannot):
  ```
  dotnet build src\LocalScribe.App
  ```
  Expect `Build succeeded.` (close any running `LocalScribe.App.exe` first - MSB3027).

- [ ] 4. Manual verification (view-layer only, no automated test - see Task 5's runbook entry "V1" for the full checklist item this satisfies):
  - [ ] Run the app; confirm the Sessions page action bar button reads "Import audio or video..." and, on click, the dialog's title bar reads "Import audio or video".

- [ ] 5. Commit:
```
git add src\LocalScribe.App\ImportDialog.xaml src\LocalScribe.App\Pages\SessionsPage.xaml
git commit -m "polish(import): dialog title and button copy mention video is now accepted"
```

---

### Task 5: Full-suite regression run + smoke-runbook checklist

**Files:**
- Modify: none in `src` expected; fix-forward anything the full suites surface (any genuine defect lands with its own failing-test-first cycle; a stale/flaky assert gets a plain, justified update only).
- Modify: `docs\plans\2026-08-02-ux-round-smoke-runbook.md` (append a new section at the end of the file, after the existing "3.x End-to-end dropdown sweep" section which currently ends at line 43 - the file EXISTS, this is an append, never a recreate).

**Interfaces:** none new.

**Steps:**

- [ ] 1. Close any running `LocalScribe.App.exe` (`tasklist | findstr LocalScribe` then close that specific process only - never kill all npm/tauri/dotnet processes broadly).

- [ ] 2. Run the FULL Core suite (no `Category` filter - this intentionally includes the Fixture-tagged tests, since `tools\ffmpeg` is present in this worktree):
  ```
  dotnet test tests\LocalScribe.Core.Tests
  ```
  Baseline measured on this worktree at HEAD `8050a61` (2026-08-02, before this plan): `Failed: 2, Passed: 1052, Skipped: 0, Total: 1054` - the 2 known PRE-EXISTING environmental failures are `DiarisationFixtureTests.Der_within_baseline_plus_epsilon` (needs a self-contained published `LocalScribe.Diarizer.exe` beside the test binary, not built by a plain `dotnet build`) and `GoldenCorpusFixtureTests.Golden_pair_wer_stays_at_baseline` (needs a private golden-audio corpus under `models\golden`, not present in this repo). Task 2 above adds one new passing fixture test. Expected result after this plan: `Failed: 2, Passed: 1053, Skipped: 0, Total: 1055` - **the gate is "no NEW failures": the failing test names must be EXACTLY those same two, nothing else.**

- [ ] 3. Run the FULL App suite:
  ```
  dotnet test tests\LocalScribe.App.Tests
  ```
  Baseline measured on this worktree at HEAD `8050a61`: `Failed: 0, Passed: 869, Skipped: 0, Total: 869`. Tasks 1 and 3 above add two new passing tests. Expected result after this plan: `Failed: 0, Passed: 871, Skipped: 0, Total: 871` - **no NEW failures** (in fact, none at all expected here).

- [ ] 4. If anything fails beyond the 2 known pre-existing Core ones: stop and use systematic-debugging to find the root cause before touching any code - do not loosen an assertion to make a failure disappear.

- [ ] 5. Append this section to `docs\plans\2026-08-02-ux-round-smoke-runbook.md` (after its final existing line, currently line 43):

```markdown

## V - Video import (audio-only extraction, user addition 2026-08-02)
- [ ] V1 Sessions page: the action bar button reads "Import audio or video..."; clicking it opens a dialog titled "Import audio or video"; "Choose file..." shows video containers (MP4, MOV, MKV, WEBM, AVI, WMV) alongside the existing audio ones in the file-picker's filter dropdown.
- [ ] V2 Import a real .mp4 recording (e.g. a Webex/Zoom local recording with a video track): the probe preview shows a plausible duration/size/format; Start runs Copy -> Decode -> Transcribe -> Save exactly like an audio import, with no video-specific error.
- [ ] V3 After the import completes, open the session: the transcript contains real speech text (not silence or noise pulled from the video track); the audio player plays back the extracted audio only.
- [ ] V4 Provenance: Session Details (or an exported transcript header) shows the ORIGINAL video file's name (e.g. "recording.mp4"), never a renamed or transcoded filename.
- [ ] V5 Hover the Import button while FFmpeg is present: the tooltip mentions video formats (MP4 etc.) alongside the audio ones.
```

- [ ] 6. Commit:
```
git add docs\plans\2026-08-02-ux-round-smoke-runbook.md
git commit -m "docs(import): smoke-runbook checklist for video-import audio extraction"
```

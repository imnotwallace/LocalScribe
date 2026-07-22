# Import progress bar + ETA + live transcript preview — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** During a long audio import, replace the bare "Transcribing…" label with a determinate progress bar + ETA and a live scrolling transcript preview.

**Architecture:** Surface the per-segment stream the offline pipeline already produces as an `IProgress<TranscriptionProgress>`. The runner reports cumulative-across-legs transcribed-ms + the segment text; `AudioImporter` supplies the total duration; the import dialog VM turns that into a fraction, an ETA (from the injected `TimeProvider`), and a capped preview tail; the dialog shows a determinate bar + preview while transcribing.

**Tech Stack:** .NET 10, WPF + WPF-UI 4.0.3, CommunityToolkit.Mvvm (`[ObservableProperty]`), xUnit.

## Global Constraints

- Progress reporting is **additive and optional**: every new pipeline/importer parameter is a trailing `= null` optional so existing callers and tests compile unchanged.
- Progress text is **percentage + ETA** (`"42% · ~2 min left"`), never "M:SS of M:SS" — a split-stereo import does 2× the audio duration of work, so a minutes-of-minutes readout would show a confusing doubled total.
- ETA (`elapsed × (1 − f) / f`) is shown only once `f > 0.03`; below that, text is just `"{pct}%"`.
- Preview is a **reassurance view**: last 10 lines, source-tagged only when the import has two legs, auto-scroll to newest, no editing.
- No new emoji in any test script (repo rule).
- Elapsed for the ETA uses `TimeProvider.GetUtcNow()` deltas (injected clock → deterministic tests), not wall-clock.

## File Structure

- `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs` — add `TranscriptionProgress` record + `OfflineRunOptions.TotalDurationMs` + progress reporting in the writer loop.
- `src/LocalScribe.Core/Import/AudioImporter.cs` — set `TotalDurationMs`, forward the progress sink.
- `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs` — extend the `ImportRunner` delegate; VM progress state + ETA/format/preview logic + the transcript progress sink.
- `src/LocalScribe.App/App.xaml.cs` — host lambda passes the transcript progress into `ImportAsync`.
- `src/LocalScribe.App/ImportDialog.xaml` (+ `.xaml.cs`) — determinate bar + progress text + scrolling preview; auto-scroll code-behind.
- Tests: `tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs`, `tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs`.

---

## Task 0: Feature branch + land the two already-made fixes

The crash fix (`WhisperNetEngine.DisposeAsync`) and the compact-pill icon fix (`LiveViewWindow.xaml`) are already in the working tree on `master`. Put the whole round on one branch and commit those two first.

**Files:**
- Modify (already changed, uncommitted): `src/LocalScribe.Core/Transcription/WhisperNetEngine.cs`, `src/LocalScribe.App/LiveViewWindow.xaml`
- Add (already written): `docs/plans/2026-07-22-import-progress-preview-design.md`, `docs/plans/2026-07-22-import-progress-preview-plan.md`

- [ ] **Step 1: Branch (carries the uncommitted working-tree changes)**

```bash
git checkout -b feat/import-progress-preview
```

- [ ] **Step 2: Commit the cancel-crash fix**

```bash
git add src/LocalScribe.Core/Transcription/WhisperNetEngine.cs
git commit -m "fix(import): DisposeAsync awaits WhisperProcessor.DisposeAsync (cancel mid-transcription no longer throws)"
```

- [ ] **Step 3: Commit the compact-pill icon fix**

```bash
git add src/LocalScribe.App/LiveViewWindow.xaml
git commit -m "fix(console): compact-pill buttons use PillButton so SymbolIcon renders (not tofu under Wpf.Ui Button)"
```

- [ ] **Step 4: Commit the design + plan docs**

```bash
git add docs/plans/2026-07-22-import-progress-preview-design.md docs/plans/2026-07-22-import-progress-preview-plan.md
git commit -m "docs(plans): import progress bar + ETA + live preview design and plan"
```

---

## Task 1: Pipeline reports cumulative transcription progress

**Files:**
- Modify: `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs`
- Test: `tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs`

**Interfaces:**
- Produces: `public sealed record TranscriptionProgress(long TranscribedMs, long TotalMs, string SegmentText, TranscriptSource Source)`; `OfflineRunOptions.TotalDurationMs` (`long`, init); `OfflinePipelineRunner.RunAsync(OfflineRunOptions options, CancellationToken ct, IProgress<TranscriptionProgress>? progress = null)`.

- [ ] **Step 1: Write the failing tests**

Add to `OfflinePipelineRunnerTests.cs`. The existing tests already build WAV inputs and drive a `FakeTranscriptionEngine`; mirror their setup helpers. These two assert single-leg monotonicity and two-leg no-reset cumulative progress.

```csharp
[Fact]
public async Task Progress_single_leg_is_monotonic_and_totals_to_duration()
{
    // A fake engine returns one non-empty segment per VAD segment; total duration passed in.
    var reports = new List<TranscriptionProgress>();
    var progress = new Progress<TranscriptionProgress>(reports.Add);
    // ... build a single Local WAV of ~6 s and a runner over a FakeTranscriptionEngine
    //     that yields text for each segment (reuse this file's existing helpers) ...
    var runner = MakeRunner(engine);   // existing helper pattern in this test file
    await runner.RunAsync(
        new OfflineRunOptions { LocalWavPath = localWav, TotalDurationMs = 6000 },
        default, progress);

    Assert.NotEmpty(reports);
    for (int i = 1; i < reports.Count; i++)
        Assert.True(reports[i].TranscribedMs >= reports[i - 1].TranscribedMs, "progress must not go backwards");
    Assert.All(reports, r => Assert.Equal(6000, r.TotalMs));      // 1 leg x 6000
    Assert.True(reports[^1].TranscribedMs <= 6000);
}

[Fact]
public async Task Progress_two_legs_accumulates_without_resetting()
{
    var reports = new List<TranscriptionProgress>();
    var progress = new Progress<TranscriptionProgress>(reports.Add);
    // ... build Local + Remote WAVs (~6 s each); runner over a FakeTranscriptionEngine ...
    await runner.RunAsync(
        new OfflineRunOptions { LocalWavPath = localWav, RemoteWavPath = remoteWav, TotalDurationMs = 6000 },
        default, progress);

    Assert.All(reports, r => Assert.Equal(12000, r.TotalMs));     // 2 legs x 6000
    for (int i = 1; i < reports.Count; i++)
        Assert.True(reports[i].TranscribedMs >= reports[i - 1].TranscribedMs,
            "cumulative-across-legs must never reset when the runner switches legs");
}

[Fact]
public async Task Progress_is_silent_when_total_unknown()
{
    var reports = new List<TranscriptionProgress>();
    var progress = new Progress<TranscriptionProgress>(reports.Add);
    // TotalDurationMs defaulted to 0 -> no reports
    await runner.RunAsync(new OfflineRunOptions { LocalWavPath = localWav }, default, progress);
    Assert.Empty(reports);
}
```

> Note: reuse whatever WAV-building / `MakeRunner` / `FakeTranscriptionEngine` helpers already exist in `OfflinePipelineRunnerTests.cs`; do not invent new ones. `Progress<T>` invokes its callback synchronously here because the test thread has no `SynchronizationContext`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~OfflinePipelineRunnerTests.Progress"`
Expected: FAIL — `RunAsync` has no 3-arg overload / `TranscriptionProgress` and `TotalDurationMs` undefined.

- [ ] **Step 3: Add the type and option**

At the top of `OfflinePipelineRunner.cs`, beside `OfflineRunOptions`:

```csharp
/// <summary>Fine-grained transcription progress for the import dialog (design 2026-07-22):
/// TranscribedMs is cumulative across legs (sum of per-source max EndMs); TotalMs is
/// legCount x decoded duration (0 when unknown -> not reported). SegmentText feeds the live preview.</summary>
public sealed record TranscriptionProgress(long TranscribedMs, long TotalMs, string SegmentText, TranscriptSource Source);
```

In `OfflineRunOptions` add:

```csharp
    /// <summary>Per-leg decoded duration, so RunAsync can report a determinate transcription
    /// fraction (import dialog). 0 = unknown -> no progress reported. Set by AudioImporter.</summary>
    public long TotalDurationMs { get; init; }
```

- [ ] **Step 4: Report progress from the writer loop**

Change the `RunAsync` signature to add the trailing optional param:

```csharp
    public async Task<string> RunAsync(OfflineRunOptions options, CancellationToken ct,
        IProgress<TranscriptionProgress>? progress = null)
```

Replace the writer-loop body (the `Task.Run(async () => { long lastEndMs = 0; ... })` block) with cumulative tracking:

```csharp
        var writerLoop = Task.Run(async () =>
        {
            long lastEndMs = 0;
            var maxEndBySource = new Dictionary<TranscriptSource, long>();
            long totalWorkMs = options.TotalDurationMs * sources.Count;   // 0 when unknown
            await foreach (object item in outbox.Reader.ReadAllAsync(ct))
            {
                if (item is TranscribedSegment ts)
                {
                    var line = await merger.AppendSegmentAsync(ts, ct);
                    lastEndMs = Math.Max(lastEndMs, line.EndMs);
                    lastModel = ts.ModelName;
                    lastWeightsFile = ts.WeightsFile;
                    if (progress is not null && totalWorkMs > 0)
                    {
                        maxEndBySource[line.Source] =
                            Math.Max(maxEndBySource.GetValueOrDefault(line.Source), line.EndMs);
                        progress.Report(new TranscriptionProgress(
                            maxEndBySource.Values.Sum(), totalWorkMs, line.Text, line.Source));
                    }
                }
                else if (item is string marker)
                {
                    await merger.AppendMarkerAsync(marker, lastEndMs, ct);
                }
            }
        }, ct);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~OfflinePipelineRunnerTests"`
Expected: PASS (all OfflinePipelineRunner tests, old + new).

- [ ] **Step 6: Full non-fixture Core suite (no regressions)**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "Category!=Fixture"`
Expected: PASS, 0 failed.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs
git commit -m "feat(import): OfflinePipelineRunner reports cumulative-across-legs transcription progress"
```

---

## Task 2: AudioImporter supplies total + forwards the progress sink

**Files:**
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs`

**Interfaces:**
- Consumes: `OfflineRunOptions.TotalDurationMs`, `RunAsync(options, ct, progress)` (Task 1).
- Produces: `AudioImporter.ImportAsync(ImportRequest request, IProgress<ImportStage>? progress, Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct, IProgress<TranscriptionProgress>? transcriptProgress = null)`.

- [ ] **Step 1: Add the trailing optional parameter**

Change `ImportAsync`'s signature (append the trailing optional so every existing caller/test compiles unchanged):

```csharp
    public async Task<string> ImportAsync(ImportRequest request, IProgress<ImportStage>? progress,
        Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct,
        IProgress<TranscriptionProgress>? transcriptProgress = null)
```

- [ ] **Step 2: Set the total and forward the sink**

In the transcription section, change the runner options + call:

```csharp
            var runner = new OfflinePipelineRunner(_paths, _settings, _engineFactory,
                _vadModelFactory, _hardware, _clockFactory(), pinnedTime, _appVersion);
            await runner.RunAsync(new OfflineRunOptions
            {
                ExistingSessionId = sessionId,
                LocalWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Local).WavPath,
                RemoteWavPath = legs.FirstOrDefault(l => l.Kind == SourceKind.Remote).WavPath,
                TotalDurationMs = decoded.DurationMs,          // per-leg decoded truth -> determinate bar
            }, ct, transcriptProgress);
```

Add `using LocalScribe.Core.Pipeline;` if not already present (it is — the file uses `OfflinePipelineRunner`).

- [ ] **Step 3: Build + existing importer tests (no regressions)**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "Category!=Fixture"`
Expected: PASS, 0 failed. (No new isolated test: `AudioImporter` constructs its runner internally, so the forwarding is proven by Task 1's runner test and Task 3's end-to-end VM test. Existing `AudioImporterTests` prove the new optional param didn't break the signature.)

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.Core/Import/AudioImporter.cs
git commit -m "feat(import): AudioImporter passes decoded duration + forwards transcript progress"
```

---

## Task 3: Import dialog VM — delegate, progress state, ETA/preview

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs:521` (host lambda)
- Test: `tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `TranscriptionProgress` (Task 1), `ImportAsync(..., ct, transcriptProgress)` (Task 2).
- Produces: `delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress, IProgress<TranscriptionProgress> transcriptProgress, Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct)`; VM props `TranscribeProgress` (double 0..1), `TranscribeProgressText` (string), `IsTranscribing` (bool), `PreviewLines` (`ObservableCollection<string>`).

- [ ] **Step 1: Write the failing tests**

First, update **all four existing `ImportRunner` lambdas** in `ImportDialogViewModelTests.cs` to the new arity (they ignore the new param):
- `MakeVm` default (line ~60): `runner ?? ((req, progress, tp, confirm, ct) => Task.FromResult("session-1"))`
- `Start_builds_...` (line ~119): `ImportRunner runner = (req, progress, tp, confirm, ct) => { ... }`
- `Stereo_answers_...` (line ~206): `ImportRunner runner = (req, p, tp, c, ct) => { ... }`
- `Cancel_during_import_...` (line ~226): `ImportRunner runner = async (req, p, tp, c, ct) => { ... }`

Add a controllable clock + a `time` param to `MakeVm`:

```csharp
    private sealed class AdvanceableTime : TimeProvider
    {
        private DateTimeOffset _now;
        public AdvanceableTime(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.Utc;
    }

    // MakeVm: add optional `TimeProvider? time = null` and pass `time ?? new FixedZoneTime()`
    // into the ImportDialogViewModel constructor's last argument.
```

Then add the new tests:

```csharp
[Fact]
public async Task Transcription_progress_drives_bar_eta_and_preview()
{
    var clock = new AdvanceableTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    IProgress<TranscriptionProgress>? tp = null;
    var started = new TaskCompletionSource();
    ImportRunner runner = async (req, progress, transcriptProgress, confirm, ct) =>
    {
        tp = transcriptProgress;
        progress.Report(ImportStage.Transcribe);         // starts the ETA clock at t0
        started.SetResult();
        await Task.Delay(Timeout.Infinite, ct);          // park; the test drives progress then cancels
        return "s";
    };
    var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3", time: clock);
    decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
    await vm.PickFileCommand.ExecuteAsync(null);
    vm.RecordedAtText = "2026-03-05 14:30";

    var run = vm.StartCommand.ExecuteAsync(null);
    await started.Task;
    Assert.True(vm.IsTranscribing);

    clock.Advance(TimeSpan.FromSeconds(30));
    tp!.Report(new TranscriptionProgress(15000, 60000, "hello world", TranscriptSource.Local));

    Assert.Equal(0.25, vm.TranscribeProgress, 3);        // 15000/60000
    Assert.Contains("25%", vm.TranscribeProgressText);
    Assert.Contains("left", vm.TranscribeProgressText);  // f>0.03 -> ETA shown
    Assert.Equal("hello world", Assert.Single(vm.PreviewLines));

    vm.CancelCommand.Execute(null);
    await run;
    Assert.False(vm.IsTranscribing);
}

[Fact]
public async Task Progress_below_threshold_shows_percent_only_and_preview_caps_at_ten()
{
    IProgress<TranscriptionProgress>? tp = null;
    var started = new TaskCompletionSource();
    ImportRunner runner = async (req, progress, transcriptProgress, confirm, ct) =>
    {
        tp = transcriptProgress; progress.Report(ImportStage.Transcribe); started.SetResult();
        await Task.Delay(Timeout.Infinite, ct); return "s";
    };
    var (vm, decoder, _) = MakeVm(runner, pickedPath: @"C:\a.mp3");
    decoder.Probe = new AudioProbeResult { FormatName = "mp3" };
    await vm.PickFileCommand.ExecuteAsync(null);
    vm.RecordedAtText = "2026-03-05 14:30";
    var run = vm.StartCommand.ExecuteAsync(null);
    await started.Task;

    tp!.Report(new TranscriptionProgress(500, 60000, "first", TranscriptSource.Local));   // <3%
    Assert.DoesNotContain("left", vm.TranscribeProgressText);
    Assert.Contains("1%", vm.TranscribeProgressText);

    for (int i = 0; i < 12; i++)
        tp.Report(new TranscriptionProgress(1000 * (i + 1), 60000, $"line {i}", TranscriptSource.Local));
    Assert.Equal(10, vm.PreviewLines.Count);             // capped tail
    Assert.Equal("line 11", vm.PreviewLines[^1]);        // newest kept

    vm.CancelCommand.Execute(null); await run;
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ImportDialogViewModelTests.Transcription_progress|FullyQualifiedName~ImportDialogViewModelTests.Progress_below"`
Expected: FAIL — delegate arity mismatch / `TranscribeProgress`, `IsTranscribing`, `PreviewLines` undefined.

- [ ] **Step 3: Extend the delegate + add VM state**

In `ImportDialogViewModel.cs`, change the delegate:

```csharp
public delegate Task<string> ImportRunner(ImportRequest request, IProgress<ImportStage> progress,
    IProgress<TranscriptionProgress> transcriptProgress,
    Func<DurationMismatchInfo, Task<bool>> confirmDurationMismatch, CancellationToken ct);
```

Add `using System.Collections.ObjectModel;` (present) and `using LocalScribe.Core.Pipeline;`. Add fields/props:

```csharp
    // --- transcription progress (design 2026-07-22) ---
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private double _transcribeProgress;      // 0..1 for the determinate bar
    [ObservableProperty] private string _transcribeProgressText = "";
    public ObservableCollection<string> PreviewLines { get; } = new();
    private DateTimeOffset _transcribeStartUtc;
    private bool _twoLegs;

    private void OnStageForProgress(ImportStage stage)
    {
        if (stage == ImportStage.Transcribe)
        {
            IsTranscribing = true;
            _transcribeStartUtc = _time.GetUtcNow();
        }
        else if (stage == ImportStage.Save)
        {
            IsTranscribing = false;
        }
    }

    private void OnTranscriptProgress(TranscriptionProgress p)
    {
        double f = p.TotalMs > 0 ? Math.Clamp(p.TranscribedMs / (double)p.TotalMs, 0, 1) : 0;
        TranscribeProgress = f;
        int pct = (int)Math.Round(f * 100);
        if (f > 0.03)
        {
            var elapsed = _time.GetUtcNow() - _transcribeStartUtc;
            var remaining = TimeSpan.FromMilliseconds(elapsed.TotalMilliseconds * (1 - f) / f);
            TranscribeProgressText = $"{pct}% · ~{FormatEta(remaining)} left";
        }
        else
        {
            TranscribeProgressText = $"{pct}%";
        }
        string line = _twoLegs
            ? $"{(p.Source == TranscriptSource.Remote ? "Them" : "Me")}: {p.SegmentText}"
            : p.SegmentText;
        PreviewLines.Add(line);
        while (PreviewLines.Count > 10) PreviewLines.RemoveAt(0);
    }

    private static string FormatEta(TimeSpan t) => t.TotalSeconds >= 60
        ? $"{(int)Math.Ceiling(t.TotalMinutes)} min"
        : $"{(int)Math.Ceiling(t.TotalSeconds)} sec";
```

> `·` is the middle-dot; keep it as the escape to avoid a literal non-ASCII char in source.

- [ ] **Step 4: Wire the sinks in `StartAsync`**

In `StartAsync`, reset state, set `_twoLegs`, hook the stage sink to `OnStageForProgress`, and pass the transcript sink. Replace the `_runImport(...)` call region:

```csharp
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsTranscribing = false;
        TranscribeProgress = 0;
        TranscribeProgressText = "";
        PreviewLines.Clear();
        _twoLegs = request.Stereo is StereoMapping.Split or StereoMapping.SplitSwapped;
        // (build `request` first if not already; keep the existing request construction)
        try
        {
            string id = await _runImport(request, new DispatchProgress(this),
                new TranscriptDispatchProgress(this), _confirmMismatch, _cts.Token);
```

> `request` is already built above the try in the current code; move the `_twoLegs` line to just after the request is constructed. The stage sink `DispatchProgress` must now also call `OnStageForProgress` — see Step 5.

In the `finally`, also clear transcribe state:

```csharp
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsBusy = false;
            IsTranscribing = false;
            StageText = "";
        }
```

- [ ] **Step 5: Update `DispatchProgress` + add `TranscriptDispatchProgress`**

Extend the existing `DispatchProgress` so the stage report also drives `OnStageForProgress`, and add the transcript sink (both marshal through `_dispatch`, like the existing one):

```csharp
    private sealed class DispatchProgress(ImportDialogViewModel owner) : IProgress<ImportStage>
    {
        public void Report(ImportStage value) => owner._dispatch(() =>
        {
            owner.OnStageForProgress(value);
            owner.StageText = value switch
            {
                ImportStage.Copy => "Copying original file...",
                ImportStage.Decode => "Decoding audio...",
                ImportStage.Transcribe => "Transcribing...",
                _ => "Saving session...",
            };
        });
    }

    private sealed class TranscriptDispatchProgress(ImportDialogViewModel owner)
        : IProgress<TranscriptionProgress>
    {
        public void Report(TranscriptionProgress value) => owner._dispatch(() => owner.OnTranscriptProgress(value));
    }
```

- [ ] **Step 6: Update the host lambda in `App.xaml.cs`**

At `App.xaml.cs:521`, add the `transcriptProgress` param and pass it into `ImportAsync` (5th positional arg):

```csharp
            ViewModels.ImportRunner runImport = async (req, progress, transcriptProgress, confirm, ct) =>
            {
                if (comp.Controller.State != LocalScribe.Core.Live.SessionState.Idle)
                    throw new InvalidOperationException(
                        "A live recording is in progress - stop it before importing audio.");
                if (comp.Controller.ExternalEngineBusy?.Invoke() is string engineBusy)
                    throw new InvalidOperationException(
                        $"Another engine is busy ({engineBusy}) - wait for it to finish before importing audio.");
                importBusy = "audio import";
                try { return await Task.Run(() => importer.ImportAsync(req, progress, confirm, ct, transcriptProgress), ct); }
                finally { importBusy = null; }
            };
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj --filter "FullyQualifiedName~ImportDialogViewModelTests"`
Expected: PASS (all ImportDialogViewModel tests, old + new).

> The App project's output copy will fail with MSB3027 if the app is running — that is a copy-step lock, not a compile/test failure. If tests can't run because the app holds the lock, close the running app first.

- [ ] **Step 8: Commit**

```bash
git add src/LocalScribe.App/ViewModels/ImportDialogViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/ImportDialogViewModelTests.cs
git commit -m "feat(import): import dialog VM computes progress %, ETA, and live transcript preview"
```

---

## Task 4: Import dialog UI — determinate bar + progress text + preview

**Files:**
- Modify: `src/LocalScribe.App/ImportDialog.xaml`
- Modify: `src/LocalScribe.App/ImportDialog.xaml.cs`

**Interfaces:**
- Consumes: `IsTranscribing`, `TranscribeProgress`, `TranscribeProgressText`, `PreviewLines` (Task 3).

- [ ] **Step 1: Replace the busy panel markup**

In `ImportDialog.xaml`, replace the current busy `StackPanel` (the one with `StageText` + the indeterminate `ProgressBar`) with a version that swaps to a determinate bar + preview while transcribing:

```xml
        <StackPanel Visibility="{Binding IsBusy, Converter={StaticResource BoolToVis}}" Margin="0,12,0,0">
            <TextBlock Text="{Binding StageText}" />
            <!-- Copy/Decode/Save: indeterminate. Hidden once transcription (determinate) starts. -->
            <ProgressBar IsIndeterminate="True" Height="4" Margin="0,4,0,0">
                <ProgressBar.Style>
                    <Style TargetType="ProgressBar">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IsTranscribing}" Value="True">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ProgressBar.Style>
            </ProgressBar>
            <!-- Transcribing: determinate bar + "42% . ~2 min left" + scrolling live preview. -->
            <StackPanel Visibility="{Binding IsTranscribing, Converter={StaticResource BoolToVis}}">
                <ProgressBar Minimum="0" Maximum="1" Value="{Binding TranscribeProgress}"
                             Height="4" Margin="0,4,0,0" />
                <TextBlock Text="{Binding TranscribeProgressText}" Style="{StaticResource MutedText}"
                           Margin="0,4,0,0" />
                <Border BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}" BorderThickness="1"
                        CornerRadius="4" Height="120" Margin="0,8,0,0">
                    <ScrollViewer x:Name="PreviewScroll" VerticalScrollBarVisibility="Auto" Padding="8,4">
                        <ItemsControl ItemsSource="{Binding PreviewLines}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <TextBlock Text="{Binding}" TextWrapping="Wrap"
                                               Style="{StaticResource MutedText}" Margin="0,1" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </ScrollViewer>
                </Border>
            </StackPanel>
        </StackPanel>
```

- [ ] **Step 2: Auto-scroll the preview in code-behind**

In `ImportDialog.xaml.cs` constructor, after `DataContext = vm;`, subscribe to the preview collection and scroll to the end on change:

```csharp
        vm.PreviewLines.CollectionChanged += (_, _) => PreviewScroll.ScrollToEnd();
```

> `PreviewLines` is mutated via the VM's `_dispatch` (UI thread), so `CollectionChanged` fires on the UI thread — no extra marshalling. `PreviewScroll` is the `x:Name` from Step 1.

- [ ] **Step 3: Build to validate the markup**

Run: `dotnet build src/LocalScribe.App/LocalScribe.App.csproj -v q --nologo`
Expected: either success, or **only** `MSB3027`/`MSB3021` copy-lock errors if the app is running (that means the XAML markup compiled — no `MC3xxx`/`XamlParse` errors). If any markup error appears, fix it.

- [ ] **Step 4: Commit**

```bash
git add src/LocalScribe.App/ImportDialog.xaml src/LocalScribe.App/ImportDialog.xaml.cs
git commit -m "feat(import): import dialog shows a determinate progress bar, ETA, and live transcript preview"
```

- [ ] **Step 5: Manual smoke (user, after restart)**

Close the running app, rebuild, relaunch. Import a multi-minute file: the "Transcribing…" stage now shows a filling bar, a "NN% · ~N left" readout, and transcript lines scrolling in. Cancel mid-way: the calm "Import cancelled…" info toast (from the earlier crash fix), no red error.

---

## Self-Review

**Spec coverage:**
- Determinate progress bar → Task 1 (data) + Task 4 (bar). ✓
- ETA → Task 3 (`OnTranscriptProgress`/`FormatEta`, `>0.03` gate). ✓
- Split-stereo cumulative math → Task 1 (per-source max sum; two-leg test). ✓
- Live preview (tail 10, source-tagged when 2 legs, auto-scroll) → Task 3 (`PreviewLines`) + Task 4 (ItemsControl + `ScrollToEnd`). ✓
- Percentage-not-minutes copy → Global Constraints + Task 3. ✓
- Additive/optional plumbing → Tasks 1–2 trailing `= null`; Task 3 updates every `ImportRunner` lambda. ✓
- Deterministic tests, no models → Task 1 (FakeTranscriptionEngine) + Task 3 (AdvanceableTime). ✓

**Placeholder scan:** Task 1 Step 1 intentionally reuses this-file WAV/`MakeRunner` helpers rather than reprinting them (they already exist in `OfflinePipelineRunnerTests.cs`); every code step that introduces new code shows it in full.

**Type consistency:** `TranscriptionProgress(TranscribedMs, TotalMs, SegmentText, Source)`, `TotalDurationMs`, `RunAsync(options, ct, progress)`, `ImportAsync(..., ct, transcriptProgress)`, `ImportRunner(request, progress, transcriptProgress, confirm, ct)`, VM `TranscribeProgress`/`TranscribeProgressText`/`IsTranscribing`/`PreviewLines` — names identical across all tasks.

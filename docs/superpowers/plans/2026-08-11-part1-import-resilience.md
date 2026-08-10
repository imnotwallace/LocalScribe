# Part 1 — Import Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An import must never destroy work — a mid-run engine downgrade that cannot find weights must not crash, and no failure may delete a session whose audio is already on disk.

**Architecture:** Four independent defects compound today. Fix them from the inside out: make engine recreation survivable (stops the crash), make the model ladder consult the disk (stops the pointless downgrade), disarm a realtime heuristic that has no meaning offline (stops the trigger), and salvage the session when transcription dies anyway (stops the loss). Each is separately testable and separately valuable.

**Tech Stack:** C# / .NET 10 (`net10.0-windows`), xunit 2.9.3, Whisper.net, existing `FakeEngineFactory` / `FakeTranscriptionEngine` / `FakeClock` doubles in `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs`.

**Spec:** `docs/superpowers/specs/2026-08-11-import-resilience-line-insertion-lean-export-design.md` §Part 1.

## Global Constraints

- **ASCII source only.** Non-ASCII characters in `src/` must be `\uXXXX` escapes — there is a byte-scan gate that fails the build otherwise. See the note at the top of `ExportNotices.cs`.
- **Never drop audio** (owner decision 2026-07-02). A transcription fault must leave capture and retained audio intact.
- **Evidentiary invariant.** `transcript.jsonl` is append-only; never rewritten, reordered, or tombstoned.
- **A mid-session weights change is evidence.** It must produce a marker, never a silent swap.
- **Test gate:** `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — 2,553 tests, ~63 s. Do **not** pass `--no-build` from a stale tree; `BuildVersionTests` derives its expected version from `src/Directory.Build.props` and fails against stale binaries.
- **Do not run the app while building.** A running `LocalScribe.App.exe` locks `Core.dll` and produces MSB3027.
- Commit after every task.

---

### Task 1: Make engine recreation survivable

The crash-stopper, and worth doing first: with this in place a downgrade onto missing weights becomes a logged non-event even before the ladder is fixed.

`RecreateAsync` disposes the working engine *before* building its replacement, with no `try`/`catch`, so a creation failure leaves nothing to continue on and the exception escapes `RunAsync`. The correct shape already exists 40 lines below in `TrySwapEngineForLanguageLockAsync`, which its own doc says handles "a missing weight file (e.g. only .en models fetched)". Apply that shape to the downgrade path.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs:214-228`
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`

**Interfaces:**
- Consumes: `ITranscriptionEngine`, `BackendPlan`, `ModelLadder.Downgrade(string)` (unchanged this task), `ErrorRaised : Action<string>?`.
- Produces: no new public surface. Behaviour change only — `DowngradeAsync` returns the still-live `current` engine instead of throwing when the replacement cannot be created.

- [ ] **Step 1: Write the failing test**

Add to `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`:

```csharp
[Fact]
public async Task Downgrade_onto_missing_weights_keeps_transcribing_on_the_current_engine()
{
    var clock = new FakeClock();
    var errors = new List<string>();
    // small.en works; the ladder's next rung (base.en) has no weights file on disk.
    var factory = new FakeEngineFactory(plan => plan.ModelName == "small.en"
        ? new FakeTranscriptionEngine("small.en", new object[]
          {
              new VramOutOfMemoryException("cuda alloc failed"),   // forces one DowngradeAsync
              new TranscriptionResult("after the failed downgrade", "en", 0.01),
          })
        : throw new FileNotFoundException("Model file missing: ggml-base.en.bin"));
    var worker = Worker(factory, clock);
    worker.ErrorRaised += errors.Add;
    var got = new List<TranscribedSegment>();
    worker.SegmentTranscribed += got.Add;

    var run = worker.RunAsync(default);
    await worker.EnqueueAsync(Seg(0), default);
    worker.Complete();
    await run;                                    // must COMPLETE, not throw

    Assert.Equal("after the failed downgrade", Assert.Single(got).Result.Text);
    Assert.Contains("MODEL_DOWNLOAD_FAILED", errors);
}
```

- [ ] **Step 2: Run it and confirm it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~Downgrade_onto_missing_weights"`
Expected: FAIL — the `FileNotFoundException` escapes `RunAsync` and `await run` throws.

- [ ] **Step 3: Implement create-before-dispose in the downgrade path**

Replace `DowngradeAsync` / `RecreateAsync` at `TranscriptionWorker.cs:214-228`:

```csharp
    /// <summary>One ladder step, then rebuild the engine. A downgrade is a RESPONSE to trouble and
    /// must never become trouble of its own: the replacement is created BEFORE the current engine
    /// is disposed, and a creation failure - missing weights for the next rung is the live case
    /// (2026-08-11) - reverts the plan and keeps transcribing on the engine already in hand. The
    /// pre-2026-08-11 shape disposed first and let the throw escape RunAsync, which cost an entire
    /// near-complete import.</summary>
    private async Task<ITranscriptionEngine> DowngradeAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        var previousPlan = _plan;
        string? next = ModelLadder.Downgrade(_plan.ModelName);
        _plan = next is not null
            ? _plan with { ModelName = next }
            : _plan with { Backend = Backend.Cpu };     // at the floor: fall to CPU (design)
        Volatile.Write(ref _effectiveBackend, (int)_plan.Backend);   // B1-1: publish the current backend
        return await RecreateAsync(current, previousPlan, ct);
    }

    /// <summary>Create-before-dispose, mirroring TrySwapEngineForLanguageLockAsync. On failure the
    /// plan (and the published effective backend) revert, the matching error code is raised, and
    /// the caller keeps the engine it passed in.</summary>
    private async Task<ITranscriptionEngine> RecreateAsync(
        ITranscriptionEngine current, BackendPlan previousPlan, CancellationToken ct)
    {
        ITranscriptionEngine replacement;
        try
        {
            replacement = await CreateEngineAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _plan = previousPlan;
            Volatile.Write(ref _effectiveBackend, (int)_plan.Backend);
            ErrorRaised?.Invoke(ex is FileNotFoundException ? "MODEL_DOWNLOAD_FAILED" : "BACKEND_INIT_FAILED");
            return current;
        }
        await current.DisposeAsync();
        return Adopt(replacement);
    }
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~Downgrade_onto_missing_weights"`
Expected: PASS.

- [ ] **Step 5: Prove the test can fail**

Temporarily revert `RecreateAsync` to dispose-then-create, re-run, confirm FAIL, then restore. A test that passes with the bug present is worthless — this repo has shipped one before (a vacuous cancellation test, 2026-08-07).

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"`
Expected: all green. The VRAM-OOM ladder-walk tests around `TranscriptionWorkerTests` exercise `RecreateAsync` heavily; if any now fail, the create-before-dispose ordering has changed how many times `FakeEngineFactory.CreateCalls` increments — check the assertion, not the implementation.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
git commit -m "fix(transcription): a failed downgrade must not kill the run

RecreateAsync disposed the working engine before building its
replacement, so a downgrade onto a model with no weights on disk left
nothing to continue on and the FileNotFoundException escaped RunAsync -
destroying a near-complete import. Adopt the create-before-dispose shape
TrySwapEngineForLanguageLockAsync has used since 8.2: revert the plan,
raise MODEL_DOWNLOAD_FAILED, keep transcribing on the engine in hand."
```

---

### Task 2: Teach the model ladder to consult the disk

`ModelLadder.Downgrade` steps by name through `["large-v3-turbo", "large-v3", "medium", "small", "base", "tiny"]` and never asks whether the next rung's weights exist. `ModelPaths.AvailableModels()` already computes that set and `BackendSelector` already consumes it — the ladder is the one component that doesn't.

The old single-argument overload is **deleted**, not kept. Leaving a disk-blind overload available is precisely the bug.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/ModelFileResolver.cs` (add `IsAvailable`)
- Modify: `src/LocalScribe.Core/Transcription/ModelLadder.cs:16-23`
- Modify: `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs:73,210` (existing `Downgrade` call sites)
- Test: `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs`

**Interfaces:**
- Consumes: `ModelFileResolver.CandidateFiles(Backend, string) : IReadOnlyList<string>` (existing).
- Produces:
  - `ModelFileResolver.IsAvailable(Backend backend, string modelName, Func<string,bool> exists) : bool`
  - `ModelLadder.Downgrade(string modelName, Func<string,bool> isAvailable) : string?` — replaces the one-argument overload.

- [ ] **Step 1: Write the failing tests**

Add to `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs`:

```csharp
[Fact]
public void Downgrade_skips_rungs_whose_weights_are_not_on_disk()
{
    // Only "small" is installed: turbo must step straight past large-v3 and medium.
    Assert.Equal("small", ModelLadder.Downgrade("large-v3-turbo", m => m == "small"));
}

[Fact]
public void Downgrade_returns_null_when_no_lower_rung_is_installed()
{
    // The live 2026-08-11 case: large-v3-turbo is the only model on disk. Null means
    // "at the floor" and the worker falls to CPU on the SAME weights, which works.
    Assert.Null(ModelLadder.Downgrade("large-v3-turbo", m => m == "large-v3-turbo"));
}

[Fact]
public void Downgrade_preserves_the_en_suffix_and_will_not_cross_to_multilingual_weights()
{
    // A bundled base.en must NOT satisfy a multilingual walk: switching a multilingual run
    // onto English-only weights mid-session is the language-lock fix-up's decision, not the
    // ladder's. "base" is absent, so the walk continues past it.
    Assert.Null(ModelLadder.Downgrade("medium", m => m == "base.en"));
    Assert.Equal("base.en", ModelLadder.Downgrade("medium.en", m => m == "base.en"));
}

[Fact]
public void Downgrade_returns_null_for_an_unknown_model_name()
    => Assert.Null(ModelLadder.Downgrade("not-a-model", _ => true));

[Fact]
public void IsAvailable_accepts_a_quantized_only_disk()
{
    // A q8_0-only disk must read as available: quantization is a per-backend file detail,
    // not a different model (ModelFileResolver.cs:11-16).
    Assert.True(ModelFileResolver.IsAvailable(Backend.Cpu, "small.en",
        f => f == "ggml-small.en-q8_0.bin"));
    Assert.False(ModelFileResolver.IsAvailable(Backend.Cpu, "small.en", _ => false));
}
```

Then update the two existing call sites so they keep asserting pure name-table stepping:

- `BackendSelectorTests.cs:73` — `=> Assert.Equal(expected, ModelLadder.Downgrade(from, _ => true));`
- `BackendSelectorTests.cs:210` — `Assert.Equal(expected, ModelLadder.Downgrade(from, _ => true));`

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~BackendSelectorTests"`
Expected: FAIL to compile — `Downgrade` takes one argument, `IsAvailable` does not exist.

- [ ] **Step 3: Add the availability helper**

Append to `src/LocalScribe.Core/Transcription/ModelFileResolver.cs`, inside the class:

```csharp
    /// <summary>True when ANY file this backend would accept for the model exists - the plain
    /// ggml file or any known quantized variant. The ladder asks this before stepping onto a rung:
    /// a name-only step onto absent weights threw FileNotFoundException out of the worker and cost
    /// a whole import (2026-08-11).</summary>
    public static bool IsAvailable(Backend backend, string modelName, Func<string, bool> exists)
    {
        foreach (string candidate in CandidateFiles(backend, modelName))
            if (exists(candidate)) return true;
        return false;
    }
```

- [ ] **Step 4: Make the ladder disk-aware**

Replace `Downgrade` in `src/LocalScribe.Core/Transcription/ModelLadder.cs`:

```csharp
    /// <summary>The next INSTALLED rung below <paramref name="modelName"/>, or null when none is
    /// on disk. Null is a valid, working answer: the worker reads it as "at the floor" and falls
    /// to CPU on the current weights (TranscriptionWorker.DowngradeAsync).
    ///
    /// There is deliberately NO disk-blind overload. Until 2026-08-11 this stepped by name alone,
    /// and on a machine holding only ggml-large-v3-turbo.bin it returned "large-v3" - which the
    /// factory could not load, throwing out of the worker and deleting a near-complete import.
    /// BackendSelector had consulted ModelPaths.AvailableModels since design section 1; the ladder
    /// simply never did.
    ///
    /// <paramref name="isAvailable"/> takes a canonical model NAME (e.g. "medium.en"), not a file
    /// name - callers resolve quantized variants via ModelFileResolver.IsAvailable.</summary>
    public static string? Downgrade(string modelName, Func<string, bool> isAvailable)
    {
        bool en = modelName.EndsWith(".en", StringComparison.Ordinal);
        string stem = en ? modelName[..^3] : modelName;
        int i = Array.IndexOf(Rungs, stem);
        if (i < 0) return null;
        for (int next = i + 1; next < Rungs.Length; next++)
        {
            string candidate = en ? Rungs[next] + ".en" : Rungs[next];
            if (isAvailable(candidate)) return candidate;
        }
        return null;
    }
```

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~BackendSelectorTests"`
Expected: PASS. If the build fails elsewhere, a production caller of the old overload is unfixed — Task 3 covers the only one (`TranscriptionWorker`). Fix that call site now if the compiler points at it.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Transcription/ModelLadder.cs src/LocalScribe.Core/Transcription/ModelFileResolver.cs tests/LocalScribe.Core.Tests/BackendSelectorTests.cs
git commit -m "fix(transcription): the downgrade ladder must consult the disk

Downgrade stepped by name through a fixed rung table and never asked
whether the next rung's weights existed. On a machine holding only
ggml-large-v3-turbo.bin it returned large-v3 and the factory threw.
Walk past absent rungs; null (= fall to CPU on current weights) when
none is installed. The disk-blind overload is deleted, not kept."
```

---

### Task 3: Wire availability into the worker's downgrade

The ladder now demands a predicate. The worker is its only production caller, and it must answer for the backend it would actually load on.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs` (options record + `DowngradeAsync`)
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`

**Interfaces:**
- Consumes: `ModelFileResolver.IsAvailable` and `ModelLadder.Downgrade(string, Func<string,bool>)` from Task 2.
- Produces: `TranscriptionWorkerOptions.ModelAvailable : Func<Backend, string, bool>`, defaulting to a real on-disk probe through `ModelPaths`. Tests override it; nothing else needs to.

- [ ] **Step 1: Write the failing test**

Add to `TranscriptionWorkerTests.cs`:

```csharp
[Fact]
public async Task Downgrade_steps_to_the_next_INSTALLED_rung_not_merely_the_next_named_one()
{
    var clock = new FakeClock();
    var created = new List<string>();
    var factory = new FakeEngineFactory(plan =>
    {
        created.Add(plan.ModelName);
        return plan.ModelName == "small.en"
            ? new FakeTranscriptionEngine("small.en", new object[]
              {
                  new VramOutOfMemoryException("out of memory"),
                  new TranscriptionResult("recovered", "en", 0.01),
              })
            : new FakeTranscriptionEngine(plan.ModelName, _ => new TranscriptionResult("recovered", "en", 0.01));
    });
    // base.en is NOT installed; tiny.en is. The ladder must skip base.en entirely.
    var options = new TranscriptionWorkerOptions
    {
        ModelAvailable = (_, model) => model == "small.en" || model == "tiny.en",
    };
    var worker = Worker(factory, clock, options);

    var run = worker.RunAsync(default);
    await worker.EnqueueAsync(Seg(0), default);
    worker.Complete();
    await run;

    Assert.Equal(new[] { "small.en", "tiny.en" }, created);
    Assert.DoesNotContain("base.en", created);
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~next_INSTALLED_rung"`
Expected: FAIL to compile — `ModelAvailable` is not a member of `TranscriptionWorkerOptions`.

- [ ] **Step 3: Add the option**

Add to `TranscriptionWorkerOptions` in `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs` (after `MaxOomRetries`):

```csharp
    /// <summary>Is this model loadable on this backend? The ladder consults it before stepping
    /// onto a rung (2026-08-11). Defaults to a real probe of both model roots, counting any known
    /// quantized variant - so a q8_0-only disk still reads as installed. Overridden in tests, and
    /// the ONLY reason this is a delegate rather than a direct ModelPaths call.</summary>
    public Func<Backend, string, bool> ModelAvailable { get; init; } =
        static (backend, model) => ModelFileResolver.IsAvailable(
            backend, model, f => File.Exists(ModelPaths.Resolve(f)));
```

- [ ] **Step 4: Use it in the downgrade**

In `DowngradeAsync` (rewritten in Task 1), replace the `ModelLadder.Downgrade` call:

```csharp
        string? next = ModelLadder.Downgrade(_plan.ModelName, m => _o.ModelAvailable(_plan.Backend, m));
```

- [ ] **Step 5: Run the test and confirm it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~next_INSTALLED_rung"`
Expected: PASS.

- [ ] **Step 6: Run the full Core suite**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"`
Expected: all green. Existing ladder-walk tests construct `TranscriptionWorkerOptions` without `ModelAvailable`, so they now hit the real disk probe — if any fail, give that test an explicit `ModelAvailable = (_, _) => true`, which is what it always implicitly assumed.

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
git commit -m "fix(transcription): the worker answers the ladder's availability question

TranscriptionWorkerOptions.ModelAvailable defaults to a real probe of
both model roots (quantized variants included) and is what the ladder
consults before stepping onto a rung."
```

---

### Task 4: Disarm the realtime-lagging downgrade for offline runs

`LaggingRtfThreshold = 1.0` over 8 consecutive segments fires a model downgrade. That is a live-capture concept: it means "we are falling behind the microphone". An import of a finished file cannot fall behind anything, and on a long file it is a near-certain trigger into the downgrade path.

Fix it once at `OfflineRunOptions` and `RetranscriptionOptions` rather than at each call site, so every offline consumer — `AudioImporter`, the `OfflineRunner` CLI, re-transcription — is covered.

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs` (options record)
- Modify: `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs:15`
- Modify: `src/LocalScribe.Core/Retranscription/RetranscriptionRunner.cs:22`
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`, `tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs`

**Interfaces:**
- Produces: `TranscriptionWorkerOptions.LaggingDowngradeEnabled : bool` (default `true` — live capture keeps today's behaviour).

- [ ] **Step 1: Write the failing tests**

Add to `TranscriptionWorkerTests.cs`:

```csharp
[Fact]
public async Task Lagging_downgrade_does_not_fire_when_disabled()
{
    var clock = new FakeClock();
    var created = new List<string>();
    var markers = new List<string>();
    var factory = new FakeEngineFactory(plan =>
    {
        created.Add(plan.ModelName);
        return new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.Advance(5000);                 // RTF 5.0 on a 1000 ms segment: way over threshold
            return new TranscriptionResult($"seg@{s.StartMs}", "en", 0.01);
        });
    });
    var worker = Worker(factory, clock, new TranscriptionWorkerOptions
    {
        LaggingDowngradeEnabled = false,
        ModelAvailable = (_, _) => true,
    });
    worker.MarkerRaised += markers.Add;

    var run = worker.RunAsync(default);
    for (int i = 0; i < 20; i++) await worker.EnqueueAsync(Seg(i * 1000), default);
    worker.Complete();
    await run;

    Assert.Equal(new[] { "small.en" }, created);          // never recreated
    Assert.DoesNotContain(Markers.TranscriptionLagging, markers);
}
```

Add to `tests/LocalScribe.Core.Tests/OfflinePipelineRunnerTests.cs`:

```csharp
[Fact]
public void Offline_runs_disable_the_realtime_lagging_downgrade_by_default()
{
    // An import of a finished file cannot fall behind live audio. Defaulting this at the
    // options record covers AudioImporter, the OfflineRunner CLI and anything else offline.
    Assert.False(new OfflineRunOptions().Worker.LaggingDowngradeEnabled);
    Assert.True(new TranscriptionWorkerOptions().LaggingDowngradeEnabled);   // live is unchanged
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~Lagging_downgrade_does_not_fire|FullyQualifiedName~disable_the_realtime_lagging"`
Expected: FAIL to compile — `LaggingDowngradeEnabled` does not exist.

- [ ] **Step 3: Add the switch and gate the trigger**

In `TranscriptionWorkerOptions`, after `LaggingRearmLimit`:

```csharp
    /// <summary>Is the sustained-RTF downgrade armed? TRUE for live capture, where RTF above 1.0
    /// means the transcriber is falling behind the microphone. FALSE for every offline run
    /// (2026-08-11): an import of a finished file has no realtime constraint, being slower than
    /// realtime is normal, and firing here walked the model ladder for no reason - straight into
    /// the crash this round fixes. VRAM-OOM downgrade stays armed in both modes; that one is a
    /// real resource limit, not a pacing heuristic.</summary>
    public bool LaggingDowngradeEnabled { get; init; } = true;
```

Gate the trigger at `TranscriptionWorker.cs:143`:

```csharp
                if (_o.LaggingDowngradeEnabled
                    && _laggingFirings < _o.LaggingRearmLimit
                    && _rtfWindow.Count >= _o.LaggingWindow
                    && _rtfWindow.All(r => r > _o.LaggingRtfThreshold))
```

Flip the default for offline in `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs:15`:

```csharp
    public TranscriptionWorkerOptions Worker { get; init; } = new() { LaggingDowngradeEnabled = false };
```

and in `src/LocalScribe.Core/Retranscription/RetranscriptionRunner.cs:22`:

```csharp
    public TranscriptionWorkerOptions Worker { get; init; } = new() { LaggingDowngradeEnabled = false };
```

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~Lagging_downgrade_does_not_fire|FullyQualifiedName~disable_the_realtime_lagging"`
Expected: PASS.

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"`
Expected: all green. Any existing offline test asserting a lagging downgrade was asserting the bug — read it before changing it, and if it is genuinely about live capture, move it to a live fixture rather than re-enabling the flag.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs src/LocalScribe.Core/Retranscription/RetranscriptionRunner.cs tests/LocalScribe.Core.Tests/
git commit -m "fix(pipeline): the realtime-lagging downgrade is a live-only concept

An offline import cannot fall behind live audio, but it inherited
LaggingRtfThreshold=1.0 from the default options and fired a model
downgrade on any long file. Defaulted off at OfflineRunOptions and
RetranscriptionOptions so every offline consumer is covered at once.
VRAM-OOM downgrade stays armed - that one is a real resource limit."
```

---

### Task 5: Salvage the session when transcription dies

Owner decision: **keep the session, mark the gap.** `AudioImporter`'s catch-all currently deletes the whole session folder, costing the copied source, the decoded legs and every transcribed segment. The worker already does the right thing for live capture under the 2026-07-02 "audio is never dropped" ruling; the import path is the outlier.

Split by how far the import got. Before the audio legs exist there is nothing worth keeping; after, the session must survive as a **complete, valid, finalized, sealed** session — not a half-written folder the recovery scanner will later adopt.

**Files:**
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs:140-261`
- Test: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs`

**Interfaces:**
- Consumes: `Markers.TranscriptionFailed` (exists, `"transcription failed"`), `TranscriptStore.AppendAsync`, `TranscriptLine.Marker`, `SessionWriter.RegenerateProjectionsAsync`, `SessionStore.SaveAsync`.
- Produces: no new public surface. `ImportAsync` still returns the session id on success and still throws on failure — the difference is what survives on disk.

- [ ] **Step 1: Write the failing test**

First extend the existing `MakeImporter` helper (`AudioImporterTests.cs:85-90`) so a test can supply its own engine factory — it currently hardcodes `new EchoFactory()`:

```csharp
    private AudioImporter MakeImporter(FakeDecoder decoder, Settings? settings = null,
        IReadOnlySet<string>? models = null, IEngineFactory? engines = null)
        => new(_paths, settings ?? new Settings { Language = "en" }, decoder, engines ?? new EchoFactory(),
            () => new EnergyProbe(), new StaticHardwareProbe(new HardwareInfo(false, 0, false, 4)),
            () => new FakeClock(), new FixedZoneTime(), appVersion: "0.2.0-test",
            availableModels: () => models ?? new HashSet<string> { "base.en", "tiny.en", "small.en" });
```

Add a two-burst WAV helper beside `WriteBurstWav`, so the engine can succeed once and then fault — proving the *partial* transcript survives, not merely the folder:

```csharp
    /// <summary>200 ms silence + tone + 1000 ms gap + tone + 1000 ms tail: EnergyProbe yields TWO
    /// segments, so a scripted engine can transcribe one and then fault.</summary>
    private string WriteTwoBurstWav(string name, int rate = 16000)
    {
        string path = Path.Combine(_root, name);
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 1));
        int silence = rate / 5, speech = rate * 3 / 2, gap = rate, tail = rate;
        var buf = new float[silence + speech + gap + speech + tail];
        for (int f = 0; f < speech; f++)
        {
            float v = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * f / rate));
            buf[silence + f] = v;
            buf[silence + speech + gap + f] = v;
        }
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }
```

Then the two tests:

```csharp
[Fact]
public async Task A_transcription_fault_keeps_the_session_its_audio_and_a_marker()
{
    string source = Path.Combine(_root, "salvage.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteTwoBurstWav("decoded-salvage.wav"),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 5200, ClaimedChannels = 1 },
    };
    // First segment transcribes; the second faults - exactly as a missing-weights downgrade did.
    var engines = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
        new object[]
        {
            new TranscriptionResult("first segment survived", "en", 0.01),
            new InvalidOperationException("engine exploded mid-run"),
        }));

    await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder, engines: engines)
        .ImportAsync(Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

    string sessionDir = Assert.Single(Directory.GetDirectories(_paths.SessionsDir));
    string id = Path.GetFileName(sessionDir);
    Assert.True(Directory.Exists(_paths.SourceDir(id)));                      // the archived copy survived
    Assert.True(File.Exists(_paths.TranscriptJsonl(id)));
    Assert.True(File.Exists(Path.Combine(sessionDir, "manifest.json")));      // finalized AND sealed

    var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
    Assert.NotNull(record!.EndedAtUtc);        // finalized: RecoveryScanner must NOT adopt it later

    var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
    Assert.Contains(lines, l => l.Kind == TranscriptKind.Segment
                             && l.Text.Contains("first segment survived", StringComparison.Ordinal));
    Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
                             && l.Text.Contains(Markers.TranscriptionFailed, StringComparison.Ordinal));
}

[Fact]
public async Task A_failure_BEFORE_any_audio_is_written_still_deletes_the_folder()
{
    string source = Path.Combine(_root, "early-fail.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-early.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
        // Dies during decode - before ChannelMapper writes any leg, so nothing is worth keeping.
        BeforeDecode = _ => throw new InvalidDataException("decode blew up"),
    };

    await Assert.ThrowsAnyAsync<Exception>(() => MakeImporter(decoder)
        .ImportAsync(Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

    Assert.Empty(Directory.GetDirectories(_paths.SessionsDir));
}
```

> `FakeDecoder.BeforeDecode` is `Func<CancellationToken, Task>?`; throwing synchronously from the lambda is fine because `DecodeAsync` awaits it. `StoragePaths.SessionsDir` is a property, not a method.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImporterTests"`
Expected: the first test FAILS — the session directory does not exist, because the catch deleted it.

- [ ] **Step 3: Implement salvage**

In `ImportAsync`, track whether the audio legs exist. After the `ChannelMapper.WriteLegs` call at `:194-195`, add:

```csharp
            legsWritten = true;      // past here a failure is salvageable, not disposable
```

declaring `bool legsWritten = false;` beside `string? sessionId = null;` at `:143`.

Replace the catch at `:251-256`:

```csharp
        catch (Exception ex)
        {
            // Owner decision 2026-08-11: an import must never destroy work. Once the audio legs
            // exist, everything transcribed so far plus the archived source is worth more than a
            // clean slate - the same "audio is never dropped" ruling (2026-07-02) the live worker
            // already honours. Only a failure BEFORE any audio is written leaves nothing to keep.
            if (sessionId is not null)
            {
                if (legsWritten && ex is not OperationCanceledException)
                {
                    try { await SalvageAsync(sessionId, decodedDurationMs, ex, ct); }
                    catch { /* salvage is best-effort; never mask the original fault */ }
                }
                else
                {
                    try { Directory.Delete(_paths.SessionDir(sessionId), recursive: true); } catch { }
                }
            }
            throw;
        }
```

`decodedDurationMs` is a `long?` declared beside `legsWritten` and assigned `decoded.DurationMs` right after the decode at `:180`. A declined duration-mismatch gate is an `OperationCanceledException` and must still delete — the user asked for nothing to be kept.

Add the salvage method after `ImportAsync`:

```csharp
    /// <summary>Turn a faulted import into a COMPLETE, valid session rather than a folder the
    /// recovery scanner will later adopt: mark the transcript at the failure point, finalize with
    /// EndedAtUtc set, recount, and regenerate projections (which reseals manifest.json last).
    /// Re-transcription is already versioned, so that is the recovery route for the missing tail.</summary>
    private async Task SalvageAsync(string sessionId, long? decodedDurationMs, Exception cause,
        CancellationToken ct)
    {
        var transcript = new TranscriptStore(_paths.TranscriptJsonl(sessionId));
        var lines = await transcript.ReadAllAsync(ct);
        long lastMs = lines.Where(l => l.Kind == TranscriptKind.Segment)
                           .Select(l => l.EndMs).DefaultIfEmpty(0).Max();
        await transcript.AppendAsync(TranscriptLine.Marker(
            await transcript.NextSeqAsync(ct), lastMs,
            $"{Markers.TranscriptionFailed}: {cause.Message}"), ct);

        var sessionStore = new SessionStore(_paths.SessionJson(sessionId));
        if (await sessionStore.ReadAsync(ct) is { } record)
        {
            long durationMs = decodedDurationMs ?? lastMs;
            lines = await transcript.ReadAllAsync(ct);
            await sessionStore.SaveAsync(record with
            {
                DurationMs = durationMs,
                EndedAtUtc = record.StartedAtUtc.AddMilliseconds(durationMs),
                SegmentCount = lines.Count(l => l.Kind == TranscriptKind.Segment),
                MarkerCount = lines.Count(l => l.Kind == TranscriptKind.Marker),
            }, ct);
        }
        await new SessionWriter(_paths, _settings, _machineTime)
            .RegenerateProjectionsAsync(sessionId, ct);
    }
```

> `Markers.TranscriptionFailed` is a bare constant with no format placeholder, so the reason is appended with `": "` rather than `string.Format`. Keep the message ASCII.

- [ ] **Step 4: Run the tests and confirm they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AudioImporterTests"`
Expected: PASS, both tests.

- [ ] **Step 5: Prove the salvaged session is genuinely openable**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"`
Expected: all green. Then confirm by inspection that the salvaged folder contains `session.json`, `meta.json`, `transcript.jsonl`, `transcript.md`, `transcript.txt`, `session.txt`, `manifest.json`, `source/` and the retained leg — a session missing `manifest.json` is not sealed and will read as tampered.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Import/AudioImporter.cs tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -m "fix(import): a failed import keeps its audio and its partial transcript

The catch-all deleted the entire session folder, so a fault at minute 27
of a 28-minute import cost the archived source, the decoded legs and
every transcribed segment. Once the legs exist, salvage instead: mark
the transcript at the failure point, finalize with EndedAtUtc set,
recount and reseal. Only a failure before any audio is written - or a
declined duration-mismatch gate - still deletes."
```

---

### Task 6: Close the two adjacent holes in the import's failure handling

Both were found while measuring the reported bug and both are in the same method.

**`Directory.CreateDirectory(workDir)` at `:142` sits OUTSIDE the try**, so a failure there is covered by neither the folder-delete catch nor the workDir-delete finally. This is the shape of the second failure family in the owner's log (`UnauthorizedAccessException` from `Directory.CreateDirectory` inside `ImportAsync`, 2026-08-07 13:38:41Z).

**There is no pre-flight**: the import attempts an ~850 MB copy before discovering the storage root is unwritable.

**Files:**
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs:140-144`
- Test: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs`

**Interfaces:**
- Produces: no new public surface. `ImportAsync` throws `InvalidOperationException` with an actionable message when the destination is unwritable or short of space, instead of an `UnauthorizedAccessException` from deep inside.

- [ ] **Step 1: Write the failing test**

Add a `paths` parameter to `MakeImporter` so a test can point the importer at a bad root (keep the `engines` parameter added in Task 5):

```csharp
    private AudioImporter MakeImporter(FakeDecoder decoder, Settings? settings = null,
        IReadOnlySet<string>? models = null, IEngineFactory? engines = null, StoragePaths? paths = null)
        => new(paths ?? _paths, settings ?? new Settings { Language = "en" }, decoder, engines ?? new EchoFactory(),
```

The test needs no special permissions — a directory *underneath a regular file* can never be created, which is exactly the "unwritable destination" condition and is hermetic on any account:

```csharp
[Fact]
public async Task An_unwritable_storage_root_fails_before_any_copy_with_an_actionable_message()
{
    string source = Path.Combine(_root, "unwritable.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    // A FILE where a directory would have to go: CreateDirectory under it always throws IOException.
    string blocker = Path.Combine(_root, "blocker.txt");
    await File.WriteAllTextAsync(blocker, "x");
    var badPaths = new StoragePaths(Path.Combine(blocker, "store"));
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-unwritable.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
    };

    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        MakeImporter(decoder, paths: badPaths).ImportAsync(
            Request(source), null, _ => Task.FromResult(true), CancellationToken.None));

    Assert.Contains("storage", ex.Message, StringComparison.OrdinalIgnoreCase);
    Assert.False(Directory.Exists(Path.Combine(blocker, "store")));
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~unwritable_storage_root"`
Expected: FAIL — an `UnauthorizedAccessException` (or `DirectoryNotFoundException`) escapes instead.

- [ ] **Step 3: Pre-flight, and move the workDir creation inside the try**

Replace `AudioImporter.cs:140-144`:

```csharp
        // Pre-flight before an ~850 MB copy: a storage root that cannot be created or written
        // produced an UnauthorizedAccessException from deep inside ImportAsync (owner log,
        // 2026-08-07), after the user had already waited through the file picker.
        long needBytes = new FileInfo(request.SourcePath).Exists
            ? new FileInfo(request.SourcePath).Length * 2   // archived copy + decoded WAV headroom
            : 0;
        EnsureWritable(_paths.SessionsDir, needBytes);

        string workDir = Path.Combine(Path.GetTempPath(), "localscribe-import",
            Guid.NewGuid().ToString("N"));
        string? sessionId = null;
        bool legsWritten = false;
        long? decodedDurationMs = null;
        try
        {
            Directory.CreateDirectory(workDir);   // INSIDE the try: the finally must be able to clean it up
```

and add:

```csharp
    /// <summary>Fail fast and legibly on the two things that make a long import pointless: a
    /// destination that cannot be written, and a volume without room for it.</summary>
    private static void EnsureWritable(string dir, long needBytes)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".ls-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The storage folder '{dir}' cannot be written to. Check the storage location in "
                + "Settings, and that the drive is connected and you have permission to write to it.", ex);
        }

        if (needBytes <= 0) return;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
            if (drive.IsReady && drive.AvailableFreeSpace < needBytes)
                throw new InvalidOperationException(
                    $"Not enough free space on {drive.Name} to import this file: about "
                    + $"{needBytes / (1024 * 1024)} MB is needed.");
        }
        catch (ArgumentException) { /* unmappable root (UNC): skip the space check, not the import */ }
    }
```

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~unwritable_storage_root"`
Expected: PASS.

- [ ] **Step 5: Run the full Core suite**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Import/AudioImporter.cs tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -m "fix(import): pre-flight the destination, and clean up the work dir

CreateDirectory(workDir) sat outside the try, so a failure there was
covered by neither cleanup path. Move it in, and check the storage root
is writable with room to spare BEFORE copying ~850 MB."
```

---

### Task 7: Record why a downgrade happened

The owner's log shows the crash and not its cause: `VRAM_OOM` and `RTF_LAGGING` are raised as `ErrorRaised` events that reach no log. Diagnosing this bug required reading a stack trace and inferring the trigger.

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs` (or wherever the offline/import worker's `ErrorRaised` is subscribed — grep for `ErrorRaised +=`)
- Modify: `src/LocalScribe.Core/Transcription/TranscriptionWorker.cs` (`DowngradeAsync`: raise a code naming the outcome)
- Test: `tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs`

**Interfaces:**
- Produces: two new `ErrorRaised` codes — `"MODEL_DOWNGRADED"` (a rung was taken; payload names from→to) and `"MODEL_DOWNGRADE_FLOOR"` (no installed rung below; fell to CPU on the current model).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_downgrade_with_no_installed_rung_reports_reaching_the_floor()
{
    var clock = new FakeClock();
    var errors = new List<string>();
    var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName,
        new object[]
        {
            new VramOutOfMemoryException("out of memory"),
            new TranscriptionResult("ok", "en", 0.01),
        }));
    var worker = Worker(factory, clock, new TranscriptionWorkerOptions
    {
        ModelAvailable = (_, model) => model == "small.en",     // nothing below is installed
    });
    worker.ErrorRaised += errors.Add;

    var run = worker.RunAsync(default);
    await worker.EnqueueAsync(Seg(0), default);
    worker.Complete();
    await run;

    Assert.Contains("VRAM_OOM", errors);
    Assert.Contains("MODEL_DOWNGRADE_FLOOR", errors);
}
```

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~reports_reaching_the_floor"`
Expected: FAIL — `MODEL_DOWNGRADE_FLOOR` is never raised.

- [ ] **Step 3: Raise the codes**

In `DowngradeAsync`, after resolving `next`:

```csharp
        ErrorRaised?.Invoke(next is not null ? "MODEL_DOWNGRADED" : "MODEL_DOWNGRADE_FLOOR");
```

- [ ] **Step 4: Route the worker's error codes to the diagnostic log**

Find the import/offline subscription (`grep -rn "ErrorRaised +=" src/`) and add a `IDiagnosticLog.Write` alongside the existing handling, at `warn` level, source `"transcription"`, message the code. Do not include transcript text — the redaction rules exist for a reason and the code alone is the diagnostic value.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: all green, 2,553 tests.

- [ ] **Step 6: Commit**

```bash
git add -A src/ tests/
git commit -m "feat(diagnostics): log why the engine downgraded

VRAM_OOM and RTF_LAGGING were raised as events that reached no log, so
the diagnostic record held the crash and not its cause. Add
MODEL_DOWNGRADED / MODEL_DOWNGRADE_FLOOR and route the worker's codes
to IDiagnosticLog."
```

---

### Task 8 (optional — owner's call): probe and decode the same audio stream

`FfmpegAudioDecoder` reads only the **first** audio stream (`:99-110`, `break; // first audio stream only`) while the decode invocation carries no `-map` (`:36-37`) and lets ffmpeg select its own best stream. When those differ, the recorded `decodedChannels` / `decodedSampleRate` and the duration gate describe a stream that was not decoded. The reported file has one audio track so this did not fire, but Axon Body units ship multi-track variants and the owner's corpus is Axon body-worn footage.

**Files:**
- Modify: `src/LocalScribe.Core/Import/FfmpegAudioDecoder.cs:31-39,78-118`
- Modify: `src/LocalScribe.Core/Model/SessionRecord.cs` (`ImportedSourceInfo`: record the chosen stream index)
- Test: `tests/LocalScribe.Core.Tests/AudioImportFixtureTests.cs` (`[Trait("Category","Fixture")]`)

- [ ] **Step 1: Write the failing fixture test**

`AudioImportFixtureTests` already synthesises its media with real ffmpeg and needs no model weights (`:130-133` is the MP4 idiom). Add a two-audio-track variant whose **first** track is mono and whose second is stereo — ffmpeg's own default selection prefers the stream with more channels, so probe (first) and decode (best) disagree:

```csharp
[Fact]
[Trait("Category", "Fixture")]
public async Task RealFfmpeg_decodes_the_SAME_audio_stream_it_probed()
{
    string tools = RequireFfmpegTools();          // existing helper in this file
    string mono = WriteFixtureWav("track-a.wav", channels: 1);
    string stereo = WriteFixtureWav("track-b.wav", channels: 2);

    // Stream order: 0:v video, 0:a:0 MONO, 0:a:1 STEREO. ffmpeg's default audio pick is the
    // stream with the most channels (the stereo one), while ParseProbeJson breaks on the first.
    string mp4 = Path.Combine(_root, "two track bodycam.mp4");
    var encode = Process.Start(new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe"),
        $"-v error -nostdin -y -i \"{mono}\" -i \"{stereo}\" " +
        $"-f lavfi -i \"color=c=black:s=64x64:r=1\" -shortest " +
        $"-map 2:v -map 0:a -map 1:a -c:v mpeg4 -pix_fmt yuv420p -c:a aac -b:a 96k \"{mp4}\"")
        { UseShellExecute = false })!;
    await encode.WaitForExitAsync();
    Assert.Equal(0, encode.ExitCode);

    string id = await MakeFixtureImporter().ImportAsync(
        new ImportRequest
        {
            SourcePath = mp4, Title = "Two-track fixture",
            RecordedAtLocal = new DateTimeOffset(2026, 3, 5, 14, 30, 0, TimeSpan.FromHours(10)),
            MatterIds = [], Stereo = StereoMapping.Downmix,
        },
        null, _ => Task.FromResult(true), CancellationToken.None);

    var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
    // The recorded channel count must describe the stream that was ACTUALLY decoded. Before the
    // fix this records 1 (probed from stream 0) while ffmpeg decoded the 2-channel stream.
    Assert.Equal(1, session!.ImportedSource!.DecodedChannels);
    Assert.Equal("mono", session.ImportedSource.ChannelMapping);
}
```

> `RequireFfmpegTools`, `WriteFixtureWav` and `MakeFixtureImporter` stand for whatever the existing two facts in this file already use to locate ffmpeg, synthesise a WAV and build the importer — reuse them verbatim rather than adding parallel helpers. If `WriteFixtureWav` has no channel parameter, widen it the way `AudioImporterTests.WriteBurstWav` is widened.

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category=Fixture&FullyQualifiedName~AudioImportFixtureTests"`
Expected: FAIL — probe and decode disagree.

- [ ] **Step 3: Choose the stream once and use it in both calls**

Keep "first audio stream" as the selection rule — this task is about probe and decode *agreeing*, not about changing which track wins.

In `ParseProbeJson` (`FfmpegAudioDecoder.cs:99-110`), capture the chosen stream's **audio-relative** index as it breaks, and surface it on `AudioProbeResult`:

```csharp
    /// <summary>Index of the chosen stream AMONG THE AUDIO STREAMS (the n in "-map 0:a:n"), or
    /// null when the file has no audio. Recorded because ffprobe and ffmpeg pick independently:
    /// ffprobe took the first audio stream while the decode carried no -map and let ffmpeg choose
    /// its own best (most channels), so on a multi-track body-worn file the recorded channels,
    /// sample rate and duration gate described a stream that was never decoded (2026-08-11).</summary>
    public int? AudioStreamIndex { get; init; }
```

Thread it into the decode. `DecodeAsync` currently builds its arguments at `:36-37`; add the map immediately before `-vn`:

```csharp
        string map = probe.AudioStreamIndex is { } ai ? $"-map 0:a:{ai} " : "";
        // ...
            $"-v error -nostdin -y -i \"{path}\" {map}-vn -acodec pcm_s16le \"{outPath}\"";
```

`DecodeAsync` takes only `(path, workDir, ct)` today, so it must either re-probe or accept the probe result. Prefer passing it: change `IAudioDecoder.DecodeAsync` to take the `AudioProbeResult` the caller already holds (`AudioImporter.cs:152` probes, `:180` decodes), and update `FakeDecoder` in `AudioImporterTests` to match. Re-probing inside the decoder would spawn a second ffprobe on an ~850 MB file for no reason.

Record the chosen index on `ImportedSourceInfo` so the session states which track it transcribed.

- [ ] **Step 4: Run the fixture test and confirm it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Category=Fixture&FullyQualifiedName~AudioImportFixtureTests"`
Expected: PASS. If the repo walk-up cannot find `tools\ffmpeg`, set `LOCALSCRIBE_FFMPEG`.

- [ ] **Step 5: Run the full gate and commit**

```bash
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
git add -A src/ tests/
git commit -m "fix(import): probe and decode the SAME audio stream

ffprobe read the first audio stream while ffmpeg picked its own, so on a
multi-track body-worn file the recorded channels, sample rate and
duration gate described a stream that was never decoded."
```

---

### Task 9 (optional — owner's call): seal imported audio

Both import finalize calls omit `sealAudio` (`OfflinePipelineRunner.cs:225`, `AudioImporter.cs:247`), which defaults to `false` (`SessionWriter.cs:38`), and `ManifestBuilder.cs:100` then skips any never-sealed leg. **Verified on the owner's disk:** an imported session's `manifest.json` lists `edits.json`, `meta.json`, `session.json`, `speakers.json` and `transcript.jsonl` — and not `local.flac`. So `Verify integrity` makes no claim about the audio of any imported session.

**Files:**
- Modify: `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs:225`
- Modify: `src/LocalScribe.Core/Import/AudioImporter.cs:247-248`
- Test: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs`

- [ ] **Step 1: Write the failing test**

`ManifestBuilderTests.cs:22` shows the reader convention — `new ManifestStore(_paths.ManifestJson(id))`:

```csharp
[Fact]
public async Task An_imported_session_seals_its_retained_audio()
{
    string source = Path.Combine(_root, "sealed.mp3");
    await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
    var decoder = new FakeDecoder
    {
        DecodedWavPath = WriteBurstWav("decoded-sealed.wav", 16000, 1, 0),
        Probe = new AudioProbeResult { FormatName = "mp3", ClaimedDurationMs = 2700, ClaimedChannels = 1 },
    };

    string id = await MakeImporter(decoder).ImportAsync(
        Request(source), null, _ => Task.FromResult(true), CancellationToken.None);

    var manifest = await new ManifestStore(_paths.ManifestJson(id)).ReadAsync(default);
    var leg = Assert.Single(manifest!.Files, f => f.Name == "local.flac");
    Assert.False(string.IsNullOrEmpty(leg.Sha256));
}
```

> Check `ManifestStore`'s read method name and signature at its definition before writing the test — `ManifestBuilderTests` is the reference for both it and the field-by-field assertion style (that file warns explicitly against `Assert.Equal` over a whole `SessionManifest`).

- [ ] **Step 2: Run and confirm failure**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~seals_its_retained_audio"`
Expected: FAIL — `local.flac` is absent from the manifest.

- [ ] **Step 3: Pass sealAudio on both import finalize calls**

`AudioImporter.cs:247-248`:

```csharp
            await new SessionWriter(_paths, _settings, _machineTime)
                .RegenerateProjectionsAsync(sessionId, ct, sealAudio: true);
```

and the equivalent at `OfflinePipelineRunner.cs:225`. Check the actual parameter name and position at `SessionWriter.cs:38` before editing.

- [ ] **Step 4: Run the test and confirm it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~seals_its_retained_audio"`
Expected: PASS.

- [ ] **Step 5: Check the cost**

Sealing hashes the FLAC. Confirm the fixture import test has not slowed materially — if it has, note the measured cost in the commit message so the trade is on the record.

- [ ] **Step 6: Run the full gate and commit**

```bash
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
git add -A src/ tests/
git commit -m "fix(import): seal imported audio into the integrity manifest

Both import finalize calls omitted sealAudio, so ManifestBuilder skipped
the retained leg and Verify integrity made no claim about the audio of
ANY imported session - the exact evidentiary guarantee the product
exists to provide."
```

---

## Verification before calling Part 1 done

- [ ] `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` — 2,553+ tests green, no `--no-build`.
- [ ] `dotnet test LocalScribe.slnx --filter "Category=Fixture"` — the import fixtures pass (needs `tools\ffmpeg`; models not required for `AudioImportFixtureTests`).
- [ ] Build warnings at or below baseline.
- [ ] **Real-file smoke, which no test replaces:** import the 893 MB 28-minute body-worn MP4 that motivated this round, on `large-v3-turbo` with only that model installed. It must complete. Then re-run with the models folder temporarily renamed so the ladder finds nothing, and confirm the run still completes on CPU rather than dying.
- [ ] Confirm `{StorageRoot}\diagnostics\diag-YYYYMM.jsonl` now records the downgrade reason.
- [ ] Consider setting `logging.includeTranscriptText: true` temporarily during the smoke — with it `false`, exception messages log as `[redacted]` and a failure will again hide its own cause.

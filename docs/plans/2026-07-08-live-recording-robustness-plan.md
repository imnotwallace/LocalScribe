# Live-Recording Robustness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three live-recording defects found by real-hardware diagnostics: (1) `Model=auto` records dead air when the selected model isn't downloaded; (2) a wrong/silent capture endpoint isn't surfaced during recording; (3) a transcription-engine failure destroys the raw audio recording.

**Architecture:** Model selection becomes availability-aware and Start fails fast on a missing model (never records dead air). `LiveSourcePipeline` splits its single cancellation token into a **capture** token (audio, Stop/Pause-scoped) and a **feed** token (VAD→worker, worker-fault-scoped) so raw FLAC keeps recording when the transcriber dies. `SessionController` gains a segment-presence monitor (driven by the existing per-frame peak events) that raises a persistent silent-leg indicator. Core stays WPF-free; the fake capture provider + a faulting `FakeEngineFactory` drive the unit tests.

**Tech Stack:** C# / .NET 10, NAudio, Whisper.net, Silero VAD (onnxruntime), WPF + WPF-UI, xUnit. Core is WPF-free and headless-testable.

## Global Constraints

- **Never record dead air.** If the resolved Whisper model file is absent at Start, refuse to start (clear message, `State` stays `Idle`, no session folder) — never create a session that can't transcribe.
- **Auto downgrades, explicit refuses.** `Model=auto` resolves to the best model **present on disk** at/below the hardware tier; an explicit (non-auto) pick that isn't downloaded refuses at Start.
- **Raw audio is evidentiary and must survive a transcriber failure.** A transcription-engine fault keeps audio recording, surfaces a warning, and finalizes cleanly on Stop (`recovered == false`) with a `transcription failed` marker — never lose the FLAC, never crash Stop.
- **Byte-identity on the healthy path.** When nothing faults, the two-token split and the new monitor must not change transcript/audio output — existing `SessionController`/`LiveSourcePipeline` tests stay green.
- **Core stays WPF-free.** Everything under `src/LocalScribe.Core` has no WPF references.
- **No Unicode emojis in test scripts** (user rule).
- **Zero-warning build gate:** `dotnet build LocalScribe.slnx -c Debug --nologo` = 0 warnings / 0 errors. App tests green; Core has 2 KNOWN pre-existing fixture fails (`Der_within_baseline_plus_epsilon`, `Golden_pair_wer_stays_at_baseline` — missing local corpus) — the bar is "no NEW failures".
- **Close `LocalScribe.App.exe` before building** — a running exe locks `Core.dll` and causes MSB3027 copy errors (NOT compile failures).

**Build/test commands** (run from `F:\LocalScribe`):
- Full build gate: `dotnet build LocalScribe.slnx -c Debug --nologo`
- Core tests: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj`
- App tests: `dotnet test tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj`
- Single class: append ` --filter "FullyQualifiedName~ClassName"`

## File Structure

**Core (modified):**
- `src/LocalScribe.Core/Transcription/ModelPaths.cs` — `AvailableModels()` (Task 1).
- `src/LocalScribe.Core/Transcription/BackendSelector.cs` — availability-aware selection + tuple result (Task 2).
- `src/LocalScribe.Core/Live/SessionController.cs` — fail-fast + downgrade notice (Task 3); two-token wiring + transcription-failed handling (Task 6); silent-leg monitor (Task 7).
- `src/LocalScribe.Core/Live/LiveSourcePipeline.cs` — two-token split (Task 5).
- `src/LocalScribe.Core/Model/Markers.cs` — `TranscriptionFailed` (Task 6).
- `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs`, `src/LocalScribe.LiveRunner/Program.cs` — `Select` call-site updates (Task 2).

**App (modified):**
- `src/LocalScribe.App/ViewModels/SessionViewModel.cs` (+ `RecordingConsoleViewModel.cs` / status strip XAML) — surface `SilentLegDetected`/`SilentLegCleared` (Task 8).

**Tooling / docs:**
- `tools/fetch-models.ps1` — add `ggml-small.en.bin` (Task 4).
- `docs/specs/localscribe-specs.md` — §2.1/§3/§8.2 deltas (Task 9).

---

## Phase 1 — Fix 1: model resolution never records dead air

### Task 1: `ModelPaths.AvailableModels()`

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/ModelPaths.cs`
- Test: `tests/LocalScribe.Core.Tests/ModelPathsTests.cs` (create)

**Interfaces:**
- Produces: `static IReadOnlySet<string> ModelPaths.AvailableModels()` — the set of present model names (basename of each `ggml-*.bin` in `ModelsRoot`, with the `ggml-` prefix and `.bin` suffix stripped, e.g. `base.en`). Empty set if the dir is missing/unreadable.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/ModelPathsTests.cs
using System.IO;
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.Core.Tests;

public class ModelPathsTests
{
    [Fact]
    public void AvailableModels_ListsPresentGgmlBasenames()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ggml-base.en.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "ggml-small.en.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "silero_vad.onnx"), "x");   // not a ggml model

            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", dir);
            var models = ModelPaths.AvailableModels();

            Assert.Contains("base.en", models);
            Assert.Contains("small.en", models);
            Assert.DoesNotContain("silero_vad", models);
            Assert.Equal(2, models.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AvailableModels_EmptyWhenDirMissing()
    {
        Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS",
            Path.Combine(Path.GetTempPath(), "ls-nope-" + Guid.NewGuid().ToString("N")));
        try { Assert.Empty(ModelPaths.AvailableModels()); }
        finally { Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null); }
    }
}
```

> `ModelPaths.ModelsRoot` honors `LOCALSCRIBE_MODELS` (env override) first — the tests use it to point at a temp dir. Tests set/clear the env var in a finally; they do not run in parallel with other ModelPaths readers by construction (each uses its own temp dir).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~ModelPathsTests"`
Expected: FAIL — `AvailableModels` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Transcription/ModelPaths.cs — add to the ModelPaths class
    /// <summary>The set of Whisper model names present on disk: each "ggml-{name}.bin" in ModelsRoot
    /// mapped to "{name}" (e.g. "base.en"). Empty if the models dir is missing/unreadable. Used by
    /// BackendSelector so "auto" only resolves to a model that can actually load (design section 1).</summary>
    public static IReadOnlySet<string> AvailableModels()
    {
        try
        {
            if (!Directory.Exists(ModelsRoot)) return new HashSet<string>();
            return Directory.EnumerateFiles(ModelsRoot, "ggml-*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (IOException) { return new HashSet<string>(); }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~ModelPathsTests"`
Expected: PASS (both facts).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Transcription/ModelPaths.cs tests/LocalScribe.Core.Tests/ModelPathsTests.cs
git commit -m "feat(core): ModelPaths.AvailableModels lists present ggml models"
```

---

### Task 2: `BackendSelector` availability-aware selection

**Files:**
- Modify: `src/LocalScribe.Core/Transcription/BackendSelector.cs`
- Modify (call sites): `src/LocalScribe.Core/Live/SessionController.cs:213`, `src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs:56`, `src/LocalScribe.LiveRunner/Program.cs:48`
- Modify (tests): `tests/LocalScribe.Core.Tests/BackendSelectorTests.cs`

**Interfaces:**
- Consumes: `ModelPaths.AvailableModels()` shape (an `IReadOnlySet<string>`) — Task 1.
- Produces: `static (BackendPlan Plan, string? DowngradedFrom) BackendSelector.Select(HardwareInfo hw, Settings settings, IReadOnlySet<string> availableModels)`. Backend choice unchanged. Model: explicit (`settings.Model != "auto"`) → that name verbatim, `DowngradedFrom == null`. Auto → the ceiling model for the backend (`Cuda`/`Vulkan`/`fastCores>=8` → `small.en`, else `base.en`); if the ceiling model is present, use it (`DowngradedFrom == null`); else the largest **present** model below the ceiling on the ladder `tiny.en < base.en < small.en`, with `DowngradedFrom` = the ceiling name; if none present at/below the ceiling, the ceiling name unchanged (Start refuses). The `.en`-trim for non-English (`settings.Language` not `en`/`auto`) applies last to the chosen name.

- [ ] **Step 1: Update the tests to the new signature + behavior**

The 6 existing `BackendSelectorTests` facts call `BackendSelector.Select(hw, S())` and read `plan.ModelName`. Update each to `var (plan, _) = BackendSelector.Select(hw, S(), Present("small.en","base.en","tiny.en"))` (all models present → behavior identical to today, so their existing model expectations hold). Add a `Present(params string[])` helper returning `new HashSet<string>(...)`. Then add new facts:

```csharp
// tests/LocalScribe.Core.Tests/BackendSelectorTests.cs — add helper + new facts
    private static IReadOnlySet<string> Present(params string[] m) => new HashSet<string>(m, StringComparer.Ordinal);

    [Fact]
    public void Auto_downgrades_to_best_present_below_ceiling()
    {
        // CUDA ceiling is small.en, but only base.en/tiny.en are present.
        var (plan, downgradedFrom) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present("base.en", "tiny.en"));
        Assert.Equal("base.en", plan.ModelName);
        Assert.Equal("small.en", downgradedFrom);
    }

    [Fact]
    public void Auto_uses_ceiling_when_present_no_downgrade()
    {
        var (plan, downgradedFrom) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present("small.en", "base.en"));
        Assert.Equal("small.en", plan.ModelName);
        Assert.Null(downgradedFrom);
    }

    [Fact]
    public void Explicit_pick_is_returned_verbatim_even_if_absent()
    {
        var (plan, downgradedFrom) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(model: "small.en"), Present("base.en"));
        Assert.Equal("small.en", plan.ModelName);   // Start validates presence, not Select
        Assert.Null(downgradedFrom);
    }

    [Fact]
    public void Auto_with_no_present_models_returns_ceiling_for_start_to_refuse()
    {
        var (plan, _) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present());
        Assert.Equal("small.en", plan.ModelName);   // absent -> Start's fail-fast handles it (Task 3)
    }
```

> Check the exact `S(...)` helper signature in the file and match it (it builds a `Settings`; add an optional `model:` param if absent). Update ALL existing call sites in this file to the tuple form.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~BackendSelectorTests"`
Expected: FAIL — 3-arg overload + tuple return don't exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Transcription/BackendSelector.cs — replace Select
public static class BackendSelector
{
    // Worst -> best. Auto never exceeds the per-backend ceiling; it downgrades within this ladder
    // to the best model actually present on disk (design section 1).
    private static readonly string[] Ladder = ["tiny.en", "base.en", "small.en"];

    public static (BackendPlan Plan, string? DowngradedFrom) Select(
        HardwareInfo hw, Settings settings, IReadOnlySet<string> availableModels)
    {
        Backend backend = settings.Backend != Backend.Auto
            ? settings.Backend
            : hw.HasCuda && hw.CudaVramMb >= 4096 ? Backend.Cuda
            : hw.HasVulkan ? Backend.Vulkan
            : Backend.Cpu;

        string? downgradedFrom = null;
        string model;
        if (settings.Model != "auto")
        {
            model = settings.Model;                     // explicit: verbatim; Start validates presence
        }
        else
        {
            string ceiling = backend switch
            {
                Backend.Cuda => "small.en",
                Backend.Vulkan => "base.en",
                _ => hw.FastCores >= 8 ? "small.en" : "base.en",
            };
            model = BestPresentAtOrBelow(ceiling, availableModels);
            if (model != ceiling) downgradedFrom = ceiling;   // record the downgrade for a Start notice
        }

        bool english = settings.Language is "en" or "auto";
        if (!english && model.EndsWith(".en", StringComparison.Ordinal))
            model = model[..^3];                        // multilingual weights (spec 3)

        return (new BackendPlan(backend, model), downgradedFrom);
    }

    private static string BestPresentAtOrBelow(string ceiling, IReadOnlySet<string> available)
    {
        int ceilingRank = Array.IndexOf(Ladder, ceiling);
        for (int r = ceilingRank; r >= 0; r--)
            if (available.Contains(Ladder[r])) return Ladder[r];
        // Nothing present at/below the ceiling: return the ceiling name unchanged so Start's
        // fail-fast (Task 3) refuses with a clear "not downloaded" message.
        return ceiling;
    }
}
```

- [ ] **Step 4: Update the three production call sites**

```csharp
// src/LocalScribe.Core/Live/SessionController.cs:213 — capture the tuple (DowngradedFrom used in Task 3)
                var (plan, downgradedFrom) = BackendSelector.Select(
                    _hardware.Probe(), settings, ModelPaths.AvailableModels());
```
(Add `using LocalScribe.Core.Transcription;` if not present — it is, via `TranscriptionWorker`.)

```csharp
// src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs:56
        var (plan, _) = BackendSelector.Select(_hardware.Probe(), _settings, ModelPaths.AvailableModels());
```

```csharp
// src/LocalScribe.LiveRunner/Program.cs:48
Console.WriteLine($"Backend plan: {BackendSelector.Select(hw, settings, ModelPaths.AvailableModels()).Plan}");
```

- [ ] **Step 5: Run tests + build**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~BackendSelectorTests"`
Expected: PASS (existing + new facts). Then `dotnet build LocalScribe.slnx -c Debug --nologo` → 0/0 (proves all call sites updated).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Transcription/BackendSelector.cs src/LocalScribe.Core/Live/SessionController.cs src/LocalScribe.Core/Pipeline/OfflinePipelineRunner.cs src/LocalScribe.LiveRunner/Program.cs tests/LocalScribe.Core.Tests/BackendSelectorTests.cs
git commit -m "feat(core): BackendSelector auto-downgrades to the best present model"
```

---

### Task 3: `SessionController` fail-fast on a missing model + downgrade notice

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` (around `:213`, before `SessionBootstrap.StartAsync`)
- Test: `tests/LocalScribe.Core.Tests/SessionControllerModelTests.cs` (create)

**Interfaces:**
- Consumes: `BackendSelector.Select(...)` tuple (Task 2); `ModelPaths.Resolve` (existing).
- Produces: in `StartAsync`, immediately after computing `(plan, downgradedFrom)` and BEFORE `SessionBootstrap.StartAsync`: if `!File.Exists(ModelPaths.Resolve($"ggml-{plan.ModelName}.bin"))` → `Notice(...)`, release the gate, return `null`, `State` stays `Idle`, no session created. Else if `downgradedFrom is not null` → `Notice(...)` and proceed.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/SessionControllerModelTests.cs
using System.IO;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-scm-" + Guid.NewGuid().ToString("N"));
    private readonly string _models = Path.Combine(Path.GetTempPath(), "ls-scm-models-" + Guid.NewGuid().ToString("N"));

    public SessionControllerModelTests()
    {
        Directory.CreateDirectory(_models);
        Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", _models);
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null);
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_models, true); } catch { }
    }

    [Fact]
    public async Task Start_refuses_when_the_resolved_model_is_absent()
    {
        // Explicit pick "base.en", but no ggml files in the models dir -> must refuse, no session.
        var settings = new Settings { Model = "base.en" };
        var (c, _, paths, _) = LiveTestDoubles.MakeController(_root, settings);
        string? notice = null;
        c.Notice += n => notice = n;

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        Assert.Null(id);                                  // refused
        Assert.Equal(SessionState.Idle, c.State);
        Assert.False(Directory.Exists(paths.SessionsDir) && Directory.EnumerateDirectories(paths.SessionsDir).Any());
        Assert.NotNull(notice);
        Assert.Contains("not downloaded", notice!);
    }

    [Fact]
    public async Task Start_proceeds_and_notices_a_downgrade_when_present()
    {
        // Auto on a CUDA box wants small.en; only base.en present -> downgrade + record.
        File.WriteAllText(Path.Combine(_models, "ggml-base.en.bin"), "x");
        var settings = new Settings { Model = "auto" };
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root, settings);   // fake provider + fake engine (no real model load)
        string? notice = null;
        c.Notice += n => notice ??= n?.Contains("better accuracy") == true ? n : notice;

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        await c.StopAsync(CancellationToken.None);

        Assert.NotNull(id);                               // recorded
        Assert.NotNull(notice);                           // downgrade notice emitted
    }
}
```

> `LiveTestDoubles.MakeController` uses `FakeEngineFactory` (never loads a real ggml), so the second test's Start does not need a real model to transcribe — but the **fail-fast check reads the real file path** via `ModelPaths.Resolve`, which is why the test writes a stub `ggml-base.en.bin` and points `LOCALSCRIBE_MODELS` at the temp dir. The controller here uses `StaticHardwareProbe(HasCuda:false,...)` per `MakeController`; adjust the expected auto model to that probe's tier (fastCores 4 → ceiling `base.en`, so writing `ggml-base.en.bin` with `auto` yields no downgrade). To force the downgrade path deterministically, use `settings = new Settings { Model = "auto", Backend = Backend.Cuda }` so the ceiling is `small.en` while only `base.en` is present. Match `MakeController`'s probe/allowed settings.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerModelTests"`
Expected: FAIL — Start currently creates the session regardless of model presence.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Live/SessionController.cs — right after the (plan, downgradedFrom) line (Task 2),
// BEFORE SessionBootstrap.StartAsync:

                string modelPath = ModelPaths.Resolve($"ggml-{plan.ModelName}.bin");
                if (!File.Exists(modelPath))
                {
                    Notice?.Invoke($"Model '{plan.ModelName}' is not downloaded. Pick an available model in " +
                                   "Settings > Transcription, or run tools/fetch-models.ps1.");
                    return null;   // refuse: no session folder, no dead-air recording (State stays Idle)
                }
                if (downgradedFrom is not null)
                    Notice?.Invoke($"Recording with {plan.ModelName}; {downgradedFrom} is not downloaded " +
                                   "(download it for better accuracy).");
```

(Add `using System.IO;` to `SessionController.cs` if not present — check the top of the file; it uses `System.Threading.Channels` etc. `File.Exists`/`ModelPaths` require `System.IO` + `LocalScribe.Core.Transcription`.)

> `return null` here is inside the `try` after `await _gate.WaitAsync`; the outer `finally { _gate.Release(); }` still runs, so the gate is released. Nothing has been created yet (this is before bootstrap), so no cleanup is needed.

- [ ] **Step 4: Run test + full Core suite**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerModelTests"` → PASS.
Then `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj` → the existing `SessionControllerTests` must stay green. Those tests call `StartAsync` with the default settings (`Model=auto`) and `MakeController`'s probe; they will now hit the fail-fast unless a model file exists. **If they regress, the fix is in the test harness, not the code:** `LiveTestDoubles.MakeController` must ensure a matching `ggml-*.bin` exists (see Step 5).

- [ ] **Step 5: Keep existing SessionController tests green (harness)**

The existing `SessionControllerTests`/`SessionControllerPauseTests` rely on `StartAsync` succeeding with `Model=auto`. Add to `LiveTestDoubles.MakeController` a stub model file so the fail-fast passes in tests that don't set `LOCALSCRIBE_MODELS` themselves:

```csharp
// tests/LocalScribe.Core.Tests/LiveTestDoubles.cs — in MakeController, before constructing the controller:
        // The Start fail-fast (SessionController model check) reads ModelPaths.Resolve; point the
        // models root at a temp dir containing a stub for the model the default auto-plan resolves to
        // on this fake probe (StaticHardwareProbe(false,0,false,4) -> ceiling base.en).
        string modelsDir = Path.Combine(root, "models");
        Directory.CreateDirectory(modelsDir);
        File.WriteAllText(Path.Combine(modelsDir, "ggml-base.en.bin"), "stub");
        File.WriteAllText(Path.Combine(modelsDir, "ggml-tiny.en.bin"), "stub");
        Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", modelsDir);
```

> This env var is process-global; `MakeController` already creates a unique `root` per test, so each test's `modelsDir` is unique. Tests that assert the refuse path (Task 3 test 1) set their OWN empty `LOCALSCRIBE_MODELS` in their ctor and use a `settings` whose model is absent there. Confirm no cross-test env-var race by running the full Core suite twice; if flakiness appears, thread the models root through `MakeController` as an explicit param instead of the env var (preferred if the harness already parameterizes paths).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerModelTests.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs
git commit -m "feat(core): Start refuses a missing model (no dead-air) + downgrade notice"
```

---

### Task 4: `fetch-models.ps1` provisions `small.en`

**Files:**
- Modify: `tools/fetch-models.ps1`
- Test: none (script; verify syntax + a dry inspection)

**Interfaces:** none.

- [ ] **Step 1: Add the small.en entry**

In `tools/fetch-models.ps1`, in the model list that contains the `ggml-tiny.en.bin` / `ggml-base.en.bin` entries, add (matching the existing entry shape exactly):

```powershell
    @{ Name = 'ggml-small.en.bin'
       Url  = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en.bin' },
```

- [ ] **Step 2: Verify the script parses**

Run: `pwsh -NoProfile -Command "$null = [ScriptBlock]::Create((Get-Content -Raw tools/fetch-models.ps1)); 'parse ok'"`
Expected: prints `parse ok` (no parse error). Do NOT actually download (466 MB) in the plan run.

- [ ] **Step 3: Commit**

```bash
git add tools/fetch-models.ps1
git commit -m "chore: fetch-models provisions ggml-small.en.bin (auto's CUDA default)"
```

---

## Phase 2 — Fix 3: raw audio survives a transcriber failure

### Task 5: `LiveSourcePipeline` two-token split (audio survives feed cancellation)

**Files:**
- Modify: `src/LocalScribe.Core/Live/LiveSourcePipeline.cs`
- Test: `tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs` (add)

**Interfaces:**
- Produces: `StartLeg(ICaptureSource source, CancellationToken captureCt, CancellationToken feedCt)` — the audio-write loop runs under `captureCt` (Stop/Pause-scoped), VAD→worker feeding is gated by `feedCt`. When `feedCt` is cancelled, the loop stops feeding VAD but **keeps writing audio + emitting peaks**. `StopLegAndFlushAsync` unchanged in signature; it ends the capture loop (bridge complete), then completes the segment channel so the VAD EOF flush still emits the trailing utterance on the clean path.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs — add
[Fact]
public async Task Audio_keeps_writing_after_the_feed_token_is_cancelled()
{
    // Simulate a worker fault: cancel ONLY the feed token mid-leg. The audio writer must keep
    // receiving frames (evidentiary audio survives a transcriber failure - design section 3).
    var (worker, _, loop, cts) = StartWorker();
    long written = 0;
    var sink = new DelegateSink(mem => written += mem.Length);
    var audioWriter = new AlignedAudioWriter(sink);
    var pipeline = new LiveSourcePipeline(SourceKind.Local, TestVad,
        () => new AmplitudeSpeechModel(), worker, audioWriter);

    using var captureCts = new CancellationTokenSource();
    using var feedCts = new CancellationTokenSource();
    // 20 speech frames then 20 silence: plenty of frames to observe writes after cancelling feed.
    var source = new FakeCaptureSource(SourceKind.Local, SpeechThenSilence(20, 20));
    pipeline.StartLeg(source, captureCts.Token, feedCts.Token);

    feedCts.Cancel();                       // "worker died" - stop feeding VAD, keep audio
    await pipeline.StopLegAndFlushAsync();  // graceful stop drains the capture loop

    worker.Complete();
    await loop;
    Assert.True(written > 0, "audio writer received no frames after the feed was cancelled");
}
```

> Confirm the existing `DelegateSink` at `LiveSourcePipelineTests.cs:120` matches `IAudioFileSink` (`Write(ReadOnlySpan<float>)` / `Write(ReadOnlyMemory<float>)`); adapt the lambda signature to the real sink interface (the file already uses it). The two existing pipeline facts (`Leg_feeds_vad_segments_into_the_worker`, `Stop_flushes_the_in_progress_utterance`) call `StartLeg(source, cts.Token)` — update them to `StartLeg(source, cts.Token, cts.Token)` (same token twice = today's behavior).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~LiveSourcePipelineTests"`
Expected: FAIL — `StartLeg` has one token; cancelling it stops the audio loop too (or the 3-arg overload doesn't exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/LocalScribe.Core/Live/LiveSourcePipeline.cs — replace StartLeg + the Tap wiring.
// The capture loop (audio + peak) runs under captureCt and ALWAYS writes; frames are pushed to a
// bounded channel drained by the segmenter+worker feed under feedCt. When feedCt trips, the loop
// stops pushing (best-effort) but keeps writing audio.
    private System.Threading.Channels.Channel<AudioFrame>? _segInput;
    private Task? _audioLoop;

    public void StartLeg(ICaptureSource source, CancellationToken captureCt, CancellationToken feedCt)
    {
        if (_legSource is not null)
            throw new InvalidOperationException($"{_source} leg already running.");

        _legSource = source;
        _bridge = new CaptureFrameBridge(source);
        _segInput = System.Threading.Channels.Channel.CreateUnbounded<AudioFrame>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var segmenter = new SileroVadSegmenter(_source, _vad, _vadModelFactory());
        _feed = Task.Run(async () =>
        {
            await foreach (var segment in segmenter.SegmentAsync(
                _segInput.Reader.ReadAllAsync(feedCt), feedCt))
                await _worker.EnqueueAsync(segment, feedCt);
        }, CancellationToken.None);

        _audioLoop = Task.Run(async () =>
        {
            await foreach (var f in _bridge.ReadAllAsync(captureCt))
            {
                _audioWriter?.Write(f);                 // ALWAYS - audio never depends on the feed
                EmitPeak(f);
                if (!feedCt.IsCancellationRequested)
                    _segInput.Writer.TryWrite(f);       // stop feeding VAD once the worker is gone
            }
            _segInput.Writer.TryComplete();             // clean EOF -> VAD Flush emits trailing utterance
        }, CancellationToken.None);

        source.Start();                                 // start LAST: bridge is already listening
    }

    private void EmitPeak(AudioFrame f)
    {
        if (PeakObserved is not { } handler) return;
        float peak = 0f;
        for (int i = 0; i < f.Samples.Length; i++)
        {
            float a = Math.Abs(f.Samples[i]);
            if (a > peak) peak = a;
        }
        handler(_source, peak);
    }
```

Rewrite `StopLegAndFlushAsync` to drain both loops in order (audio loop first so the channel completes, then the feed's VAD flush):

```csharp
    public async Task StopLegAndFlushAsync()
    {
        if (_legSource is null) return;
        _legSource.Stop();
        _bridge!.Complete();                            // ends the frame stream -> audio loop finishes
        try
        {
            if (_audioLoop is not null) await _audioLoop;   // drains capture, completes _segInput
            if (_feed is not null) await _feed;             // VAD EOF flush enqueued before this returns
        }
        finally
        {
            _bridge.Dispose();
            _legSource.Dispose();
            (_legSource, _bridge, _feed, _audioLoop, _segInput) = (null, null, null, null, null);
        }
    }
```

Delete the old `Tap(...)` method (its audio-write + peak logic moved into the audio loop + `EmitPeak`). Keep the `using System.Threading.Channels;`-free fully-qualified names or add the using at the top.

> Note on Pause/abnormal abort: normal Stop/Pause is graceful via `StopLegAndFlushAsync` (bridge complete), not via `captureCt`. `captureCt` only aborts the audio loop on a partial-Start failure (SessionController's catch cancels it). On a worker fault, only `feedCt` is cancelled — the audio loop keeps running until the graceful Stop.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~LiveSourcePipelineTests"`
Expected: PASS — new audio-survives fact + the two existing facts (updated to the 3-arg call, same-token) stay green (byte-identical clean-path behavior).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/LiveSourcePipeline.cs tests/LocalScribe.Core.Tests/LiveSourcePipelineTests.cs
git commit -m "feat(core): LiveSourcePipeline splits capture vs feed tokens (audio survives a feed abort)"
```

---

### Task 6: `SessionController` keeps recording on a transcriber fault

**Files:**
- Modify: `src/LocalScribe.Core/Model/Markers.cs`, `src/LocalScribe.Core/Live/SessionController.cs`
- Test: `tests/LocalScribe.Core.Tests/SessionControllerTranscriptionFaultTests.cs` (create)

**Interfaces:**
- Consumes: `LiveSourcePipeline.StartLeg(source, captureCt, feedCt)` (Task 5).
- Produces: `Markers.TranscriptionFailed = "transcription failed"`. `SessionController` uses a session-scoped `captureCts` (separate from `feedCts`); passes both to `StartLeg`/Resume. On worker-loop fault while Recording: set `Session.TranscriptionFailed = true`, write the `transcription failed` marker, raise `ErrorRaised("TRANSCRIPTION_FAILED")` + `Notice(...)`, stay Recording. `StopAsync` no longer rethrows a fault that the session already marked `TranscriptionFailed`; it finalizes normally (`recovered == false`, full audio, partial transcript + the marker).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/SessionControllerTranscriptionFaultTests.cs
using System.IO;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerTranscriptionFaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-txfault-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Worker_fault_keeps_audio_and_finalizes_cleanly_with_a_marker()
    {
        // Engine factory that throws on creation -> the worker faults right after Start. Audio must
        // still be written and Stop must finalize (not throw, not "recovered").
        var faulting = new FakeEngineFactory(plan => throw new FileNotFoundException("boom"));
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root, engineFactory: faulting);
        // ensure the model fail-fast passes: MakeController stubs ggml-base.en.bin (Task 3 Step 5)

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);                                     // Start succeeded (fault is async)

        string? stopped = await c.StopAsync(CancellationToken.None);   // must NOT throw
        Assert.Equal(id, stopped);

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.False(record!.Recovered);                        // clean stop, not recovery
        Assert.True(record.RetainedAudioSources.Count > 0);     // audio retained
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.TranscriptionFailed);
        // audio actually has samples: the flac exists and is > an empty-header size
        Assert.True(new FileInfo(paths.AudioFile(id!, SourceKind.Local, record.RetainedAudioSources.Contains(SourceKind.Local) ? AudioFormat.Flac : AudioFormat.Flac)).Length > 0);
    }
}
```

> Verify the exact `paths.AudioFile(...)` signature (id, SourceKind, AudioFormat) against `StoragePaths`; the audio-format is the session's `settings.AudioFormat` (default Flac). Simplify the last assertion if the signature differs — the load-bearing assertions are: Stop returns the id (no throw), `Recovered == false`, and the `transcription failed` marker is present. `FakeEngineFactory(Func<BackendPlan,ITranscriptionEngine>)` throwing in the func makes `TranscriptionWorker.RunAsync -> CreateEngineAsync` fault (matches the real missing-model fault we diagnosed).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerTranscriptionFaultTests"`
Expected: FAIL — today the worker fault surfaces at `await WorkerLoop` in `StopAsync` and is rethrown (Stop throws), and the feed cancellation stops audio.

- [ ] **Step 3: Write minimal implementation**

Add the marker:
```csharp
// src/LocalScribe.Core/Model/Markers.cs — add
    public const string TranscriptionFailed = "transcription failed";
```

In `SessionController`, add `public bool TranscriptionFailed;` to the `Session` class. In `StartAsync`:
- Create `var captureCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);` alongside `feedCts`. Store it on the `Session` (add `public required CancellationTokenSource CaptureCts;`).
- Change the leg starts to pass both tokens:
```csharp
                local.StartLeg(micSource, captureCts.Token, feedCts.Token);
                ...
                remote.StartLeg(remoteSource, captureCts.Token, feedCts.Token);
```
- Extend the worker-fault continuation so a fault ALSO flags the session + emits the marker/notice (in addition to the existing C1 feed-cancel):
```csharp
                _ = workerLoop.ContinueWith(t =>
                {
                    feedCts!.Cancel();                              // C1: unblock the feed (existing)
                    if (_session is { } s && State == SessionState.Recording)
                    {
                        s.TranscriptionFailed = true;
                        s.Outbox.Writer.TryWrite(new MarkerAt(Markers.TranscriptionFailed, s.Clock.ElapsedMs));
                        ErrorRaised?.Invoke("TRANSCRIPTION_FAILED");
                        Notice?.Invoke("Live transcription stopped - audio is still recording. You can re-transcribe this session later.");
                    }
                }, CancellationToken.None,
                   TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                   TaskScheduler.Default);
```
  (Replace the existing C1 continuation that only cancelled `feedCts` — fold both into this one. Keep `ExecuteSynchronously` semantics as before.)
- Update the partial-start `catch` (line 331-345) and the `Session` construction (line 314-322) to include/dispose `captureCts`. In the catch: `captureCts?.Cancel(); ...; captureCts?.Dispose();`.

In `StopAsync`, make the worker drain tolerant of an already-flagged fault (around line 449-452):
```csharp
                s.Worker.Complete();
                try { await s.WorkerLoop; }
                catch when (s.TranscriptionFailed) { /* already surfaced mid-session; audio-only finalize */ }
                if (legFault is not null)
                    ExceptionDispatchInfo.Capture(legFault).Throw();
```
And dispose `s.CaptureCts` alongside `s.FeedCts` in the teardown finally (line 482-484):
```csharp
                    s.FeedCts.Dispose();
                    s.CaptureCts.Dispose();
```

> The existing SILENT-source/degraded markers and the retained-audio padding (`PadToMs`) already run on the clean finalize path; with `TranscriptionFailed` the finalize is the SAME clean path (faulted stays false because we swallow the worker fault), so audio is padded and `recovered` stays false. Confirm `faulted` (the local in `StopAsync`) is NOT set on the swallowed-fault path.

- [ ] **Step 4: Run test + full Core suite**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerTranscriptionFaultTests"` → PASS.
Then the full Core suite → existing `SessionControllerTests`/`SessionControllerPauseTests` green (the healthy path is unchanged; the two-token split defaults to identical behavior; the fault continuation only fires on an actual worker fault). No NEW failures beyond the 2 known.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTranscriptionFaultTests.cs
git commit -m "feat(core): a transcriber fault keeps recording audio and finalizes cleanly"
```

---

## Phase 3 — Fix 2: sustained-no-speech indicator

### Task 7: `SessionController` silent-leg monitor

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs`, `src/LocalScribe.Core/Live/LiveSessionOptions` (add `SilentLegGraceMs`)
- Test: `tests/LocalScribe.Core.Tests/SessionControllerSilentLegTests.cs` (create)

**Interfaces:**
- Produces: events `event Action<SourceKind>? SilentLegDetected;` and `event Action<SourceKind>? SilentLegCleared;`. `LiveSessionOptions.SilentLegGraceMs { get; init; } = 15000`. Driven by the existing per-frame `PeakObserved`: on each peak while `State == Recording`, if `clock.ElapsedMs - lastSegmentMs[source] > SilentLegGraceMs` and not already flagged for that source, raise `SilentLegDetected(source)` once; when a segment from that source is inserted, update `lastSegmentMs[source]` and, if flagged, raise `SilentLegCleared(source)` + clear the flag.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.Core.Tests/SessionControllerSilentLegTests.cs
using System.IO;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerSilentLegTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-silent-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Leg_with_no_segments_past_the_grace_window_raises_SilentLegDetected()
    {
        // FakeClock is controllable; provider emits audio (peaks fire) but we make the worker yield
        // no segments so the local leg is "capturing but silent of speech".
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root,
            engineFactory: new FakeEngineFactory(_ => new SilentEngine()));   // engine that returns no segments
        var detected = new List<SourceKind>();
        c.SilentLegDetected += s => { lock (detected) detected.Add(s); };

        var options = LiveTestDoubles.Options() with { SilentLegGraceMs = 1000 };
        string? id = await c.StartAsync(options, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));   // push past the 1s grace; peaks continue to fire
        // allow the peak-driven check to run (a few frames)
        await Task.Delay(50);
        await c.StopAsync(CancellationToken.None);

        Assert.Contains(SourceKind.Local, detected);
    }
}
```

> This test needs (a) a `FakeClock.Advance` (verify the method name in the harness), (b) a `SilentEngine` test double that transcribes to empty/no-segment output (add it to `LiveTestDoubles.cs` if absent — an `ITranscriptionEngine` whose `TranscribeAsync` returns no segment, or a `FakeEngineFactory` transcribe-func returning a whitespace/empty result the worker drops). If the harness cannot cleanly produce "audio but zero segments" deterministically, drive the monitor directly: expose an internal hook, OR unit-test the pure monitor logic (a small `SilentLegMonitor` helper class: `(elapsedMs, lastSegmentMs, graceMs) -> bool`) extracted from the controller and tested in isolation. Prefer the extracted-helper approach if the end-to-end fake is fiddly — it keeps the test deterministic and the controller thin.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj --filter "FullyQualifiedName~SessionControllerSilentLegTests"`
Expected: FAIL — no `SilentLegDetected` event / `SilentLegGraceMs` option.

- [ ] **Step 3: Write minimal implementation**

Add to `LiveSessionOptions`: `public int SilentLegGraceMs { get; init; } = 15000;`.
Add the events + monitor state to `SessionController`:
```csharp
    public event Action<SourceKind>? SilentLegDetected;
    public event Action<SourceKind>? SilentLegCleared;

    // Monitor state (per active session). lastSegmentMs seeded to leg start; written by the writer
    // loop (segment insert), read on the capture thread (peak) - guard with a lock or Interlocked.
    private readonly object _silentGate = new();
    private long _lastLocalSegMs, _lastRemoteSegMs;
    private bool _localSilentFlagged, _remoteSilentFlagged;
    private int _silentGraceMs = 15000;
```
Seed `_lastLocalSegMs = _lastRemoteSegMs = 0`, `_localSilentFlagged = _remoteSilentFlagged = false`, `_silentGraceMs = options.SilentLegGraceMs` at Start (before starting legs). In the `merger.LineInserted` handler (line 232) update the per-source last-segment time and clear a flag:
```csharp
                merger.LineInserted += (i, l) =>
                {
                    LineInserted?.Invoke(i, l);
                    if (l.Kind == TranscriptKind.Segment) OnSegmentForSilentMonitor(l.Source, clock.ElapsedMs);
                };
```
In the `PeakObserved` wiring (lines 294-295) route through the monitor check:
```csharp
                local.PeakObserved += (s, p) => { PeakObserved?.Invoke(s, p); CheckSilentLeg(s, clock.ElapsedMs); };
                remote.PeakObserved += (s, p) => { PeakObserved?.Invoke(s, p); CheckSilentLeg(s, clock.ElapsedMs); };
```
Add the two helpers:
```csharp
    private void OnSegmentForSilentMonitor(TranscriptSource source, long nowMs)
    {
        var kind = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        bool wasFlagged;
        lock (_silentGate)
        {
            if (kind == SourceKind.Local) { _lastLocalSegMs = nowMs; wasFlagged = _localSilentFlagged; _localSilentFlagged = false; }
            else { _lastRemoteSegMs = nowMs; wasFlagged = _remoteSilentFlagged; _remoteSilentFlagged = false; }
        }
        if (wasFlagged) SilentLegCleared?.Invoke(kind);
    }

    private void CheckSilentLeg(SourceKind kind, long nowMs)
    {
        if (State != SessionState.Recording) return;
        bool raise = false;
        lock (_silentGate)
        {
            long last = kind == SourceKind.Local ? _lastLocalSegMs : _lastRemoteSegMs;
            bool flagged = kind == SourceKind.Local ? _localSilentFlagged : _remoteSilentFlagged;
            if (!flagged && nowMs - last > _silentGraceMs)
            {
                if (kind == SourceKind.Local) _localSilentFlagged = true; else _remoteSilentFlagged = true;
                raise = true;
            }
        }
        if (raise) SilentLegDetected?.Invoke(kind);
    }
```

> On Resume, reset `_lastLocalSegMs/_lastRemoteSegMs = s.Clock.ElapsedMs` and clear the flags so the grace window restarts (add to `ResumeAsync`). On Idle (Stop), the state is torn down; the next Start re-seeds. `SourceKind` vs `TranscriptSource` mapping: `LineInserted` carries `TranscriptSource`; peaks carry `SourceKind` — map consistently as above.

- [ ] **Step 4: Run test + full Core suite**

Run the focused filter → PASS; then the full Core suite → no new failures. If the end-to-end fake proved fiddly and you extracted a `SilentLegMonitor` helper, unit-test the helper directly and keep the controller wiring covered by a thin integration assertion.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerSilentLegTests.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs
git commit -m "feat(core): sustained-no-speech silent-leg monitor + events"
```

---

### Task 8: App surfaces the silent-leg indicator

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs` (subscribe to the events, expose an indicator), `src/LocalScribe.App/LiveViewWindow.xaml` (+ status strip) to show it
- Test: `tests/LocalScribe.App.Tests/SessionViewModelTests.cs` (add)

**Interfaces:**
- Consumes: `SessionController.SilentLegDetected`/`SilentLegCleared` (Task 7).
- Produces: `SessionViewModel` exposes `bool MicSilent`/`bool RemoteSilent` (or a single `string? SilentLegWarning`) raised from the events via the VM's dispatch; the live view binds a persistent, dismiss-on-clear warning row.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/LocalScribe.App.Tests/SessionViewModelTests.cs — add
[Fact]
public void Silent_leg_detected_sets_a_visible_warning_cleared_on_recovery()
{
    var (controller, provider, _, _) = LocalScribe.Core.Tests.LiveTestDoubles.MakeController(_root);
    var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
        startOptions: LocalScribe.Core.Tests.LiveTestDoubles.Options());

    controller.RaiseSilentLegForTest(SourceKind.Local);   // or invoke the event via a test seam
    Assert.True(vm.MicSilent);

    controller.RaiseSilentLegClearedForTest(SourceKind.Local);
    Assert.False(vm.MicSilent);
}
```

> `SessionController` events are public; if the test can't raise them directly, drive them through the fake pipeline, or add internal test seams. Match the existing `SessionViewModelTests` harness (dispatch `a => a()`, `LiveTestDoubles.MakeController`). Adjust the property name to what the VM exposes.

- [ ] **Step 2: Run test to verify it fails** — `MicSilent` doesn't exist.

- [ ] **Step 3: Write minimal implementation** — subscribe in the VM ctor (marshal via dispatch), set `[ObservableProperty] bool _micSilent` / `_remoteSilent`; unsubscribe in Dispose. Add a warning row in `LiveViewWindow.xaml` bound to the flags (a `WarningText`-styled TextBlock, visible via `BoolToVis`), e.g. "No speech detected from the microphone - check the selected device (Settings > Recording)."

- [ ] **Step 4: Run tests** — App suite green.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs src/LocalScribe.App/LiveViewWindow.xaml tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "feat(app): surface the silent-leg (no-speech) indicator during recording"
```

---

## Phase 4 — docs

### Task 9: Spec deltas

**Files:**
- Modify: `docs/specs/localscribe-specs.md`

- [ ] **Step 1: Update the spec** — §3 (auto resolves to best present model; Start refuses a missing model, no dead-air), §8.2 (add the `transcription failed` marker + sustained-no-speech indicator beside SILENT_SOURCE), §2.1 (a transcription-engine failure no longer aborts the session; audio keeps recording; finalize normally on Stop; re-transcribe offline). Match the doc's heading style.

- [ ] **Step 2: Commit**

```bash
git add docs/specs/localscribe-specs.md
git commit -m "docs(spec): model fail-fast, transcription-failed marker, silent-leg indicator"
```

---

## Final gate (after Task 9)

- [ ] **Full build:** `dotnet build LocalScribe.slnx -c Debug --nologo` → 0/0 (close `LocalScribe.App.exe` first).
- [ ] **Core suite:** green except the 2 KNOWN fixture fails; no NEW failures.
- [ ] **App suite:** all green.
- [ ] **Hardware smoke (user):** with only `base.en` present + `Model=auto` → records, downgrade notice shown; delete/rename the model → Start refuses with the message (no husk session); pin a working mic + record → transcript appears; select the silent Communications-default device → after ~15 s the "no speech" indicator shows; (hard to force) a transcriber fault keeps the FLAC. Use `LiveRunner --auto <sec>` for the non-GUI checks.

---

## Self-Review

**Spec coverage (design §1–§5):**
- §1 model fail-fast + downgrade → Tasks 1 (AvailableModels), 2 (selector), 3 (Start refuse/notice), 4 (fetch-models).
- §2 silent-leg indicator → Tasks 7 (monitor + events), 8 (App surface); Start peak warning already exists (unchanged).
- §3 audio survives transcriber fault → Tasks 5 (pipeline two-token), 6 (controller keep-recording + marker).
- §5 spec deltas → Task 9.

**Placeholder scan:** each code step carries complete code; test steps that lean on harness specifics (Tasks 3, 6, 7, 8) name the exact harness/fake to match and give a deterministic fallback (extract a pure `SilentLegMonitor`; assert the load-bearing facts). No "TBD".

**Type consistency:** `BackendSelector.Select` returns `(BackendPlan Plan, string? DowngradedFrom)` (Task 2), consumed at all three call sites + Task 3's Start. `LiveSourcePipeline.StartLeg(source, captureCt, feedCt)` (Task 5) is called by `SessionController` Start/Resume with `captureCts`/`feedCts` (Task 6). `Markers.TranscriptionFailed` (Task 6) asserted in Task 6's test. `SilentLegDetected`/`SilentLegCleared` + `SilentLegGraceMs` (Task 7) consumed by the App VM (Task 8). `ModelPaths.AvailableModels()` (Task 1) consumed by `BackendSelector` call sites (Task 2).

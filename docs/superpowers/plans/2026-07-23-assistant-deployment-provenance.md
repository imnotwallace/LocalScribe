# Assistant Helper Deployment + Backend Provenance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the deployed assistant helper actually run (CPU avx2 + CUDA), make the reported backend the one that actually ran (CUDA-to-CPU falls recorded, never silent), fail loudly on a broken deployment, and offer only models the engine can correctly prompt.

**Architecture:** Folder publish of `LocalScribe.Assistant` into an `assistant\` subfolder beside the App, with the csproj target ungated AND extended to restore the 4 conflict-dropped cuda12 natives + co-locate an avx2 `ggml-cpu.dll` (verified: the CUDA `ggml.dll` imports it at load time). The helper explicitly points LLamaSharp at `cuda12\llama.dll` when the request wants GPU and an NVIDIA driver is present (LLamaSharp's default CUDA detection needs the CUDA *toolkit* — end users never have it, verified 2026-07-23). Backend truth comes from parsing llama.cpp's `load_tensors: offloaded N/M layers to GPU` log line captured via `NativeLibraryConfig.All.WithLogCallback`.

**Tech Stack:** .NET 10 / WPF, LLamaSharp 0.25.0 (+Backend.Cpu, +Backend.Cuda12.Windows), xunit, PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-07-23-assistant-deployment-provenance-design.md` (committed @ e7a9c43), amended per the verified findings below. Read it first.

## Verified facts this plan is built on (2026-07-23, i7-9750H + GTX 1650, driver-only box)

Do NOT re-derive these; they were established by experiment before this plan was written:

1. Ungating the csproj target produces a folder publish with `runtimes/win-x64/native/{avx,avx2,avx512,noavx,cuda12}/` preserved, no NETSDK1152.
2. LLamaSharp's default probe DOES pick the CPU variant correctly from that layout by CPU detection (loaded-modules snapshot showed `avx2\llama.dll` + `avx2\ggml-cpu.dll`).
3. SDK cross-package conflict resolution drops the CUDA `llama.dll`/`ggml.dll`/`ggml-base.dll`/`mtmd.dll` (filename collisions with Backend.Cpu) BEFORE the target runs — only `ggml-cuda.dll` survives. The target must re-inject them.
4. LLamaSharp 0.25 default CUDA detection reads `CUDA_PATH` + `version.json` (toolkit-only); on driver-only boxes cuda12 is NEVER selected, even when complete and `backend=cuda` is requested — the run silently lands on CPU and `LlamaEngine` reports `"cuda"` (defect 3's second layer). `SkipCheck(true)` throws "Cannot skip the check when fallback is allowed" and is a dead end.
5. `NativeLibraryConfig.LLama.WithLibrary(<publish>\runtimes\win-x64\native\cuda12\llama.dll)` loads the CUDA backend from the runtimes layout: `found 1 CUDA devices`, `load_tensors: offloaded 37/37 layers to GPU`, 6.7s vs 30.7s CPU on the same short prompt.
6. cuda12/ MUST also contain a `ggml-cpu.dll` (avx2's): without it the CUDA `ggml.dll` itself fails to load (load-time import). The known-good flat build (`C:\temp\assistant-cuda`) contains exactly cuda12 `llama.dll` + avx2 `ggml-cpu.dll`.
7. A failed `LLama.Native.NativeApi` type initializer poisons the process — there is no in-process retry after a native load failure. The helper is spawn-per-job, so a failure is visible per job, never sticky across jobs.

## Global Constraints

- Build gate: `dotnet build LocalScribe.slnx` with **0 warnings**; full `dotnet test` green (Core has 2 known pre-existing fixture fails; App has 1 known flake `Stop_upserts...` — neither is a regression signal).
- LOCKED contracts (do not change shape): the stdio wire (`AssistantWire` request/event JSON), `AssistantDone(Backend, PromptTokens, OutputTokens)`, `AssistantModelRef(File, Sha256, Backend)`, `AssistantModelInfo`, `IAssistantJobRunner`/chat interfaces. `SummaryVersion` may gain an ADDITIVE optional member only.
- The helper writes wire events to stdout ONLY; all diagnostics/native logs go to stderr.
- Default model stays LOCKED: `Qwen3-4B-Instruct-2507` (no bake-off). The two optional models are REMOVED (spec section 6); downloaded GGUF weights are NEVER deleted by the implementation.
- ViewModels are WPF-free; dispatch injected; never `Progress<T>` (house rules).
- No Unicode emojis anywhere in test scripts (user rule).
- Evidentiary posture: degradation is surfaced, never silent; nothing persists on failure.
- Branch: `fix/assistant-deploy-provenance` off master. The working tree already carries experiment edits to `LocalScribe.Assistant.csproj` and `Program.cs` (superseded by Tasks 3/4) and pre-existing uncommitted edits to `tools/fetch-models.ps1` + the runbook (committed as part of Task 7). Untracked `isobin/` is a stray build output — delete it; leave `.ai-code-review/` alone.
- Durable artifacts (outside repo): `C:\temp\localscribe-assistant-handoff\ask-helper.ps1` (stdio smoke driver; params `-Exe -ModelPath -CtxTokens -Backend -MaxTokens -Prompt -StderrFile`), the two captured llama.cpp logs in the same folder (Task 1 commits them as fixtures), known-good flat builds `C:\temp\assistant-cuda` / `C:\temp\assistant-avx2` (controls).
- Measured baseline (1,145-token summary, 4B Q4_K_M): CUDA 37/37 = 79.9s; CPU avx2 = 111.6s; noavx = DNF in 600s. A build taking minutes on a short prompt is almost certainly noavx.

## File Structure

```
src/LocalScribe.Core/Assistant/LlamaOffloadLog.cs          (new: pure offload parse + backend rule)
src/LocalScribe.Core/Assistant/AssistantHelperLocator.cs   (new: FfmpegLocator pattern)
src/LocalScribe.Core/Assistant/AssistantPublishLayout.cs   (new: required-file list + FindMissing)
src/LocalScribe.Core/Assistant/AssistantWire.cs            (add CudaFellPhase const)
src/LocalScribe.Core/Assistant/SummaryStore.cs             (SummaryVersion additive CudaFellToCpu)
src/LocalScribe.Core/Assistant/SummarizationService.cs     (capture fall event -> version flag)
src/LocalScribe.Assistant/LocalScribe.Assistant.csproj     (ungate + extend target, comment rewrite)
src/LocalScribe.Assistant/LlamaEngine.cs                   (native config, honest backend, fall/throw)
src/LocalScribe.Assistant/Program.cs                       (drop experiment lines, keep stderr ex dump)
src/LocalScribe.App/CompositionRoot.cs                     (locator + comment rewrite)
src/LocalScribe.App/App.xaml.cs                            (chat factory helper gate, tab VM probe)
src/LocalScribe.App/ViewModels/AssistantTabViewModel.cs    (availability, explainers, fall in provenance)
src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs   (UnavailableText reword)
src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs    (AssistantHelperNote)
src/LocalScribe.App/SettingsPage.xaml                      (helper note TextBlock)
tools/verify-assistant-publish.ps1                         (new: layout guard)
tools/fetch-models.ps1                                     (drop optional models)
docs/plans/2026-07-19-llm-foundation-summaries-smoke-runbook.md (publish/deploy + section B rewrite)
tests/LocalScribe.Core.Tests/LlamaOffloadLogTests.cs       (new)
tests/LocalScribe.Core.Tests/AssistantHelperLocatorTests.cs(new)
tests/LocalScribe.Core.Tests/AssistantPublishLayoutTests.cs(new)
tests/LocalScribe.Core.Tests/Fixtures/llamacpp-log-cuda-full-offload.txt (new, committed real log)
tests/LocalScribe.Core.Tests/Fixtures/llamacpp-log-cpu-no-offload.txt    (new, committed real log)
tests/LocalScribe.Core.Tests/SummarizationServiceTests.cs  (fall-flag tests; file exists — extend)
tests/LocalScribe.App.Tests/AssistantTabViewModelTests.cs  (availability matrix + fall line; extend)
tests/LocalScribe.App.Tests/SettingsPageViewModelTests.cs  (helper note; extend — exact filename may differ, grep `SettingsPageViewModel` under tests/)
```

---

### Task 0: Branch + cleanup

**Files:** none (git only)

- [ ] **Step 1: Create the branch and delete the stray build output**

```powershell
git -C F:\LocalScribe checkout -b fix/assistant-deploy-provenance
Remove-Item -Recurse -Force F:\LocalScribe\isobin
```

Expected: branch created; `git status` no longer lists `isobin/`. Do NOT commit anything yet — the tree's uncommitted edits belong to later tasks.

---

### Task 1: Offload parser + backend rule (Core) with real-log fixtures

**Files:**
- Create: `src/LocalScribe.Core/Assistant/LlamaOffloadLog.cs`
- Modify: `src/LocalScribe.Core/Assistant/AssistantWire.cs` (add one const)
- Create: `tests/LocalScribe.Core.Tests/Fixtures/llamacpp-log-cuda-full-offload.txt` (copy of `C:\temp\localscribe-assistant-handoff\llamacpp-log-cuda-full-offload.txt`)
- Create: `tests/LocalScribe.Core.Tests/Fixtures/llamacpp-log-cpu-no-offload.txt` (copy of `C:\temp\localscribe-assistant-handoff\llamacpp-log-cpu-no-offload.txt`)
- Modify: `tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj` (fixture copy ItemGroup)
- Test: `tests/LocalScribe.Core.Tests/LlamaOffloadLogTests.cs`

**Interfaces:**
- Produces: `LlamaOffloadLog.FindOffload(string logText)` → `(int Offloaded, int Total)?`; `LlamaOffloadLog.IsFullGpu((int Offloaded, int Total)? offload)` → `bool`; `AssistantWire.CudaFellPhase` = `"cuda-fell-to-cpu"`. Task 4 (helper) and Task 6 (service/VM) consume all three.

- [ ] **Step 1: Copy the two real captured logs into the fixtures folder**

```powershell
New-Item -ItemType Directory -Force F:\LocalScribe\tests\LocalScribe.Core.Tests\Fixtures | Out-Null
Copy-Item C:\temp\localscribe-assistant-handoff\llamacpp-log-cuda-full-offload.txt F:\LocalScribe\tests\LocalScribe.Core.Tests\Fixtures\
Copy-Item C:\temp\localscribe-assistant-handoff\llamacpp-log-cpu-no-offload.txt F:\LocalScribe\tests\LocalScribe.Core.Tests\Fixtures\
```

Then add to `tests/LocalScribe.Core.Tests/LocalScribe.Core.Tests.csproj` (before `</Project>`; skip if a `Fixtures\**` copy glob already exists):

```xml
  <ItemGroup>
    <Content Include="Fixtures\**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`tests/LocalScribe.Core.Tests/LlamaOffloadLogTests.cs`:

```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public sealed class LlamaOffloadLogTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Real_cuda_full_offload_log_parses_37_of_37()
    {
        var offload = LlamaOffloadLog.FindOffload(Fixture("llamacpp-log-cuda-full-offload.txt"));
        Assert.Equal((37, 37), offload);
        Assert.True(LlamaOffloadLog.IsFullGpu(offload));
    }

    [Fact]
    public void Real_cpu_log_has_no_offload_line()
    {
        var offload = LlamaOffloadLog.FindOffload(Fixture("llamacpp-log-cpu-no-offload.txt"));
        Assert.Null(offload);
        Assert.False(LlamaOffloadLog.IsFullGpu(offload));
    }

    [Theory]
    [InlineData("load_tensors: offloaded 22/37 layers to GPU", 22, 37)]
    [InlineData("prefix noise load_tensors: offloaded 1/1 layers to GPU suffix", 1, 1)]
    public void Parses_the_offload_line_anywhere_in_the_text(string text, int offloaded, int total)
        => Assert.Equal((offloaded, total), LlamaOffloadLog.FindOffload(text));

    [Fact]
    public void Partial_offload_is_not_a_gpu_run()
        => Assert.False(LlamaOffloadLog.IsFullGpu((22, 37)));   // design section 5: mixed != GPU

    [Fact]
    public void Zero_total_is_not_a_gpu_run()
        => Assert.False(LlamaOffloadLog.IsFullGpu((0, 0)));     // total > 0 required

    [Fact]
    public void Last_offload_line_wins_when_repeated()
        => Assert.Equal((37, 37), LlamaOffloadLog.FindOffload(
            "load_tensors: offloaded 22/37 layers to GPU\nload_tensors: offloaded 37/37 layers to GPU"));

    [Fact]
    public void Fell_phase_constant_is_the_wire_literal()
        => Assert.Equal("cuda-fell-to-cpu", AssistantWire.CudaFellPhase);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter LlamaOffloadLogTests`
Expected: compile FAILURE — `LlamaOffloadLog` does not exist.

- [ ] **Step 4: Implement**

`src/LocalScribe.Core/Assistant/LlamaOffloadLog.cs`:

```csharp
using System.Text.RegularExpressions;

namespace LocalScribe.Core.Assistant;

/// <summary>Backend-provenance truth source (design 2026-07-23 section 5): llama.cpp's
/// authoritative "load_tensors: offloaded N/M layers to GPU" log line, captured by the helper
/// during load. Line PRESENT with N==M &gt; 0 -&gt; a real GPU run; partial or absent -&gt; CPU.
/// Pure text -&gt; value; the helper owns capture, this owns interpretation. Tested against the
/// two real captured logs in tests/Fixtures (a CUDA 37/37 run and a 100% CPU run).</summary>
public static partial class LlamaOffloadLog
{
    [GeneratedRegex(@"load_tensors: offloaded (\d+)/(\d+) layers to GPU")]
    private static partial Regex OffloadLine();

    /// <summary>Last match wins: the buffer is reset per load, but a single load can in
    /// principle log more than once - the final line is the settled assignment.</summary>
    public static (int Offloaded, int Total)? FindOffload(string logText)
    {
        Match? last = null;
        foreach (Match m in OffloadLine().Matches(logText)) last = m;
        return last is null ? null
            : (int.Parse(last.Groups[1].Value), int.Parse(last.Groups[2].Value));
    }

    /// <summary>The design section 5 rule: Backend = "cuda" iff offloaded == total &amp;&amp; total &gt; 0.
    /// A mixed run is NOT a GPU run.</summary>
    public static bool IsFullGpu((int Offloaded, int Total)? offload)
        => offload is { } o && o.Offloaded == o.Total && o.Total > 0;
}
```

In `src/LocalScribe.Core/Assistant/AssistantWire.cs`, directly under the `KvQuant` const add:

```csharp
    /// <summary>Progress phase emitted when an "auto" request could not run fully on the GPU
    /// and fell to CPU (design 2026-07-23 section 5) - the recorded, never-silent fall.</summary>
    public const string CudaFellPhase = "cuda-fell-to-cpu";
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter LlamaOffloadLogTests`
Expected: 8 PASS (7 facts/theories, theory counts as 2).

- [ ] **Step 6: Commit**

```powershell
git add src/LocalScribe.Core/Assistant/LlamaOffloadLog.cs src/LocalScribe.Core/Assistant/AssistantWire.cs tests/LocalScribe.Core.Tests/
git commit -m "feat(assistant): offload-line parser + cuda-fell-to-cpu phase, pinned to real llama.cpp logs"
```

---

### Task 2: AssistantHelperLocator (Core)

**Files:**
- Create: `src/LocalScribe.Core/Assistant/AssistantHelperLocator.cs`
- Test: `tests/LocalScribe.Core.Tests/AssistantHelperLocatorTests.cs`

**Interfaces:**
- Produces: `AssistantHelperLocator.FindExe()` → `string?` (production); `AssistantHelperLocator.FindExe(string baseDir, string? envOverride)` → `string?` (testable core); `AssistantHelperLocator.ExeName` = `"LocalScribe.Assistant.exe"`; `AssistantHelperLocator.MissingMessage` (names the publish command). Tasks 5 consumes `FindExe()` + `MissingMessage`.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/AssistantHelperLocatorTests.cs`:

```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public sealed class AssistantHelperLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-helper-loc-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string MakeExe(params string[] relDirParts)
    {
        string dir = Path.Combine([_root, .. relDirParts]);
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, AssistantHelperLocator.ExeName);
        File.WriteAllText(exe, "x");
        return exe;
    }

    [Fact]
    public void Env_override_wins_when_it_contains_the_exe()
    {
        string exe = MakeExe("override");
        MakeExe("base", "assistant");   // would otherwise win
        Assert.Equal(exe, AssistantHelperLocator.FindExe(
            Path.Combine(_root, "base"), Path.Combine(_root, "override")));
    }

    [Fact]
    public void Env_override_without_the_exe_is_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-override"));
        string beside = MakeExe("base", "assistant");
        Assert.Equal(beside, AssistantHelperLocator.FindExe(
            Path.Combine(_root, "base"), Path.Combine(_root, "empty-override")));
    }

    [Fact]
    public void Assistant_subfolder_beside_the_binary_is_found()
    {
        string beside = MakeExe("base", "assistant");
        Assert.Equal(beside, AssistantHelperLocator.FindExe(Path.Combine(_root, "base"), null));
    }

    [Fact]
    public void Repo_root_tools_assistant_is_the_dev_fallback()
    {
        // base dir nested under a repo root marked by LocalScribe.slnx
        File.WriteAllText(Path.Combine(_root, "LocalScribe.slnx"), "<Solution/>");
        string dev = MakeExe("tools", "assistant");
        string baseDir = Path.Combine(_root, "src", "App", "bin", "Debug");
        Directory.CreateDirectory(baseDir);
        Assert.Equal(dev, AssistantHelperLocator.FindExe(baseDir, null));
    }

    [Fact]
    public void Absent_everywhere_is_null_and_the_message_names_the_publish_command()
    {
        string baseDir = Path.Combine(_root, "lonely");
        Directory.CreateDirectory(baseDir);
        Assert.Null(AssistantHelperLocator.FindExe(baseDir, null));
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", AssistantHelperLocator.MissingMessage);
        Assert.Contains("LOCALSCRIBE_ASSISTANT", AssistantHelperLocator.MissingMessage);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantHelperLocatorTests`
Expected: compile FAILURE — `AssistantHelperLocator` does not exist.

- [ ] **Step 3: Implement**

`src/LocalScribe.Core/Assistant/AssistantHelperLocator.cs`:

```csharp
namespace LocalScribe.Core.Assistant;

/// <summary>Resolves LocalScribe.Assistant.exe the way FfmpegLocator resolves ffmpeg: the
/// LOCALSCRIBE_ASSISTANT env var (a folder containing the exe), else the "assistant\" FOLDER
/// PUBLISH beside the binary (design 2026-07-23 section 2 - a folder, NOT single-file like
/// Diarizer, because LLamaSharp probes its own runtimes/&lt;rid&gt;/native/&lt;variant&gt;/ layout
/// relative to the app directory), else "tools\assistant\" at the repo root (dev, found by
/// walking up to LocalScribe.slnx). Null when absent - the App then disables the assistant
/// with MissingMessage instead of failing on first use (which is exactly how the single-file
/// helper shipped broken).</summary>
public static class AssistantHelperLocator
{
    public const string ExeName = "LocalScribe.Assistant.exe";

    public const string MissingMessage =
        "The assistant helper is not deployed. Publish it with: dotnet publish src/LocalScribe.Assistant "
        + "-c Release -r win-x64 --self-contained -o <app folder>\\assistant, then verify with "
        + "tools/verify-assistant-publish.ps1 (or set LOCALSCRIBE_ASSISTANT to a folder containing "
        + ExeName + ").";

    public static string? FindExe()
        => FindExe(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_ASSISTANT"));

    /// <summary>Testable core; production calls the parameterless overload.</summary>
    public static string? FindExe(string baseDir, string? envOverride)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            string fromEnv = Path.Combine(envOverride, ExeName);
            if (File.Exists(fromEnv)) return Path.GetFullPath(fromEnv);
        }

        string beside = Path.Combine(baseDir, "assistant", ExeName);
        if (File.Exists(beside)) return beside;

        for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string dev = Path.Combine(d.FullName, "tools", "assistant", ExeName);
                return File.Exists(dev) ? dev : null;
            }
        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantHelperLocatorTests`
Expected: 5 PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.Core/Assistant/AssistantHelperLocator.cs tests/LocalScribe.Core.Tests/AssistantHelperLocatorTests.cs
git commit -m "feat(assistant): AssistantHelperLocator (env -> assistant subfolder -> repo dev fallback)"
```

---

### Task 3: Publish shape — csproj target + layout contract + guard script

**Files:**
- Modify: `src/LocalScribe.Assistant/LocalScribe.Assistant.csproj` (full target + comment rewrite below)
- Create: `src/LocalScribe.Core/Assistant/AssistantPublishLayout.cs`
- Create: `tools/verify-assistant-publish.ps1`
- Test: `tests/LocalScribe.Core.Tests/AssistantPublishLayoutTests.cs`

**Interfaces:**
- Produces: `AssistantPublishLayout.RequiredRelativePaths` → `IReadOnlyList<string>` (forward-slash relative paths, includes `LocalScribe.Assistant.exe`); `AssistantPublishLayout.FindMissing(string publishDir)` → `IReadOnlyList<string>` (missing OR zero-byte entries). The guard script mirrors the same list (a drift test pins them together).

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/AssistantPublishLayoutTests.cs`:

```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public sealed class AssistantPublishLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-pub-layout-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void MakeCompleteFakeTree()
    {
        foreach (string rel in AssistantPublishLayout.RequiredRelativePaths)
        {
            string p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");   // non-empty
        }
    }

    [Fact]
    public void The_contract_is_the_exact_verified_deployment_shape()
    {
        // 4 CPU variants x 5 dlls + cuda12 x 6 (incl. the co-located avx2 ggml-cpu.dll:
        // the CUDA ggml.dll imports it at LOAD time - verified 2026-07-23) + the exe.
        Assert.Equal(27, AssistantPublishLayout.RequiredRelativePaths.Count);
        Assert.Contains("LocalScribe.Assistant.exe", AssistantPublishLayout.RequiredRelativePaths);
        Assert.Contains("runtimes/win-x64/native/cuda12/ggml-cpu.dll", AssistantPublishLayout.RequiredRelativePaths);
        Assert.Contains("runtimes/win-x64/native/cuda12/llama.dll", AssistantPublishLayout.RequiredRelativePaths);
        Assert.Contains("runtimes/win-x64/native/avx2/llama.dll", AssistantPublishLayout.RequiredRelativePaths);
    }

    [Fact]
    public void Complete_tree_has_nothing_missing()
    {
        MakeCompleteFakeTree();
        Assert.Empty(AssistantPublishLayout.FindMissing(_root));
    }

    [Fact]
    public void Missing_and_empty_files_are_both_flagged()
    {
        MakeCompleteFakeTree();
        File.Delete(Path.Combine(_root, "runtimes", "win-x64", "native", "cuda12", "llama.dll"));
        File.WriteAllText(Path.Combine(_root, "runtimes", "win-x64", "native", "avx2", "ggml-cpu.dll"), "");
        var missing = AssistantPublishLayout.FindMissing(_root);
        Assert.Equal(2, missing.Count);
        Assert.Contains("runtimes/win-x64/native/cuda12/llama.dll", missing);
        Assert.Contains("runtimes/win-x64/native/avx2/ggml-cpu.dll", missing);
    }

    [Fact]
    public void Guard_script_lists_every_required_path_verbatim()
    {
        // Drift guard: tools/verify-assistant-publish.ps1 re-states the list (PowerShell cannot
        // call Core); this pins the two copies together. Repo root found the FfmpegLocator way.
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx"))) { repo = d.FullName; break; }
        Assert.NotNull(repo);
        string script = File.ReadAllText(Path.Combine(repo!, "tools", "verify-assistant-publish.ps1"));
        foreach (string rel in AssistantPublishLayout.RequiredRelativePaths)
            Assert.Contains(rel, script);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantPublishLayoutTests`
Expected: compile FAILURE — `AssistantPublishLayout` does not exist.

- [ ] **Step 3: Implement the layout contract**

`src/LocalScribe.Core/Assistant/AssistantPublishLayout.cs`:

```csharp
namespace LocalScribe.Core.Assistant;

/// <summary>The assistant helper's REQUIRED publish shape (design 2026-07-23 sections 1-2).
/// The csproj target `_PreserveLlamaCppNativeLayout` is load-bearing: if it regresses, the
/// publish silently reverts to a flattened noavx layout (measured: a 1,145-token summary that
/// avx2 finishes in 112s did not finish in 600s on noavx). This list is what the layout guard
/// asserts - tools/verify-assistant-publish.ps1 mirrors it verbatim (drift-pinned by test).
/// cuda12 carries SIX files: the four package natives + ggml-cuda.dll + a co-located avx2
/// ggml-cpu.dll, because the CUDA ggml.dll imports ggml-cpu.dll at load time (verified
/// 2026-07-23 - removing it makes the whole CUDA set fail to load).</summary>
public static class AssistantPublishLayout
{
    private static readonly string[] CpuVariants = ["avx", "avx2", "avx512", "noavx"];
    private static readonly string[] PerVariantFiles =
        ["ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll", "mtmd.dll"];
    private static readonly string[] Cuda12Files =
        ["ggml-base.dll", "ggml-cpu.dll", "ggml-cuda.dll", "ggml.dll", "llama.dll", "mtmd.dll"];

    public static readonly IReadOnlyList<string> RequiredRelativePaths =
    [
        "LocalScribe.Assistant.exe",
        .. CpuVariants.SelectMany(v => PerVariantFiles.Select(f => $"runtimes/win-x64/native/{v}/{f}")),
        .. Cuda12Files.Select(f => $"runtimes/win-x64/native/cuda12/{f}"),
    ];

    /// <summary>Missing or zero-byte required files under publishDir (forward-slash relative
    /// paths, exactly as listed). Empty = the deployment is complete.</summary>
    public static IReadOnlyList<string> FindMissing(string publishDir)
        => RequiredRelativePaths.Where(rel =>
        {
            var f = new FileInfo(Path.Combine(publishDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            return !f.Exists || f.Length == 0;
        }).ToList();
}
```

- [ ] **Step 4: Write the guard script**

`tools/verify-assistant-publish.ps1` (the path list MUST stay verbatim-identical to `AssistantPublishLayout.RequiredRelativePaths` — a unit test enforces it):

```powershell
# tools/verify-assistant-publish.ps1
# Layout guard for the assistant helper's folder publish (design 2026-07-23 section "Testing").
# The csproj target _PreserveLlamaCppNativeLayout is load-bearing; if it regresses, the publish
# silently reverts to a flattened noavx layout (a summary avx2 finishes in ~112s DNFs in 600s).
# The required list mirrors LocalScribe.Core.Assistant.AssistantPublishLayout.RequiredRelativePaths
# VERBATIM - AssistantPublishLayoutTests pins the two copies together. Update both or neither.
param([Parameter(Mandatory = $true)][string] $PublishDir)
$ErrorActionPreference = 'Stop'

$required = @(
    'LocalScribe.Assistant.exe'
    'runtimes/win-x64/native/avx/ggml-base.dll'
    'runtimes/win-x64/native/avx/ggml-cpu.dll'
    'runtimes/win-x64/native/avx/ggml.dll'
    'runtimes/win-x64/native/avx/llama.dll'
    'runtimes/win-x64/native/avx/mtmd.dll'
    'runtimes/win-x64/native/avx2/ggml-base.dll'
    'runtimes/win-x64/native/avx2/ggml-cpu.dll'
    'runtimes/win-x64/native/avx2/ggml.dll'
    'runtimes/win-x64/native/avx2/llama.dll'
    'runtimes/win-x64/native/avx2/mtmd.dll'
    'runtimes/win-x64/native/avx512/ggml-base.dll'
    'runtimes/win-x64/native/avx512/ggml-cpu.dll'
    'runtimes/win-x64/native/avx512/ggml.dll'
    'runtimes/win-x64/native/avx512/llama.dll'
    'runtimes/win-x64/native/avx512/mtmd.dll'
    'runtimes/win-x64/native/noavx/ggml-base.dll'
    'runtimes/win-x64/native/noavx/ggml-cpu.dll'
    'runtimes/win-x64/native/noavx/ggml.dll'
    'runtimes/win-x64/native/noavx/llama.dll'
    'runtimes/win-x64/native/noavx/mtmd.dll'
    'runtimes/win-x64/native/cuda12/ggml-base.dll'
    'runtimes/win-x64/native/cuda12/ggml-cpu.dll'
    'runtimes/win-x64/native/cuda12/ggml-cuda.dll'
    'runtimes/win-x64/native/cuda12/ggml.dll'
    'runtimes/win-x64/native/cuda12/llama.dll'
    'runtimes/win-x64/native/cuda12/mtmd.dll'
)

$missing = @()
foreach ($rel in $required) {
    $p = Join-Path $PublishDir ($rel -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $p) -or (Get-Item $p).Length -eq 0) { $missing += $rel }
}

if ($missing.Count -gt 0) {
    Write-Host "FAIL: assistant publish at '$PublishDir' is incomplete - missing or empty:"
    $missing | ForEach-Object { Write-Host "  $_" }
    Write-Host "The publish likely regressed to a flattened layout (see LocalScribe.Assistant.csproj target _PreserveLlamaCppNativeLayout)."
    exit 1
}
Write-Host "PASS: assistant publish layout complete ($($required.Count) required files present)."
exit 0
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantPublishLayoutTests`
Expected: 4 PASS.

- [ ] **Step 6: Rewrite the csproj target**

Replace the entire comment + target block in `src/LocalScribe.Assistant/LocalScribe.Assistant.csproj` (everything from `<!-- Single-file publish + LLamaSharp's native layout...` through `</Target>`; the tree already carries the condition-drop experiment — this replaces it wholesale). Also update the package-comment line inside the first `<ItemGroup>` that still says "Publish: single-file self-contained with IncludeNativeLibrariesForSelfExtract=true, then copy ONLY the .exe beside the App output" to:

```
         Publish: FOLDER publish into an assistant\ subfolder beside the App (design 2026-07-23) -
         single-file is IMPOSSIBLE here: LLamaSharp probes runtimes/<rid>/native/<variant>/
         relative to the app directory itself; self-extract lands the natives where that probe
         never looks and every request fails at NativeApi init. The assistant\ subfolder keeps
         this exe's own onnxruntime.dll isolated from the App's (same isolation goal as
         Diarizer's single-file rule, reached by a different means).
```

New target block:

```xml
  <!-- The folder publish and LLamaSharp's native layout need reconciling (design 2026-07-23
       section 1; replaces the former single-file-only target). The backend packages ship the
       SAME llama.cpp filenames (llama.dll, ggml.dll, ggml-base.dll, mtmd.dll) once per hardware
       variant: CPU -> runtimes/win-x64/native/{avx,avx2,avx512,noavx}/, CUDA ->
       runtimes/win-x64/native/cuda12/. Two SDK behaviors break a RID-specific publish:
       (1) native assets are flattened to bare filenames, so the variants collapse and noavx
       wins (measured: a summary avx2 finishes in ~112s did not finish in 600s on noavx);
       (2) cross-package conflict resolution drops the four CUDA natives whose names collide
       with the CPU package's BEFORE this target runs, leaving an orphaned ggml-cuda.dll.
       This target (a) restores every surviving llama.cpp native's runtimes/<rid>/native/
       <variant>/ RelativePath, (b) re-injects the conflict-dropped CUDA natives from the
       package folder (anchored off the surviving ggml-cuda.dll item, so the package path is
       never hardcoded), and (c) co-locates the avx2 ggml-cpu.dll into cuda12/ - the CUDA
       ggml.dll IMPORTS ggml-cpu.dll at load time (verified 2026-07-23: without it the whole
       CUDA set fails to load; the CPU backend also computes the non-offloaded graph parts).
       LLamaSharp's loader picks the CPU variant by runtime CPU detection (verified: avx2 on
       an AVX2 box); the CUDA path is engaged explicitly by LlamaEngine via WithLibrary,
       because LLamaSharp's own CUDA detection needs the CUDA toolkit end users don't have.
       On arm64 no cuda12 assets exist, the anchors are empty, and (b)/(c) are no-ops.
       LOAD-BEARING: if this regresses, the publish silently reverts to flattened noavx -
       tools/verify-assistant-publish.ps1 (run after every publish) exists to catch that. -->
  <Target Name="_PreserveLlamaCppNativeLayout"
          AfterTargets="ComputeResolvedFilesToPublishList">
    <ItemGroup>
      <ResolvedFileToPublish
          Condition="$([System.String]::new('%(ResolvedFileToPublish.Identity)').Replace('\', '/').Contains('/llamasharp.backend.'))">
        <RelativePath>runtimes/$([System.Text.RegularExpressions.Regex]::Match($([System.String]::new('%(ResolvedFileToPublish.Identity)').Replace('\', '/')), 'runtimes/(.*)$').Groups[1].Value)</RelativePath>
      </ResolvedFileToPublish>

      <_LlamaCuda12Anchor Include="@(ResolvedFileToPublish)"
          Condition="$([System.String]::new('%(ResolvedFileToPublish.Identity)').Replace('\', '/').Contains('/llamasharp.backend.cuda12')) And '%(ResolvedFileToPublish.Filename)%(ResolvedFileToPublish.Extension)' == 'ggml-cuda.dll'" />
      <_LlamaAvx2CpuAnchor Include="@(ResolvedFileToPublish)"
          Condition="$([System.String]::new('%(ResolvedFileToPublish.Identity)').Replace('\', '/').Contains('/avx2/ggml-cpu.dll'))" />

      <_LlamaCuda12Restored
          Include="@(_LlamaCuda12Anchor->'%(RootDir)%(Directory)llama.dll');@(_LlamaCuda12Anchor->'%(RootDir)%(Directory)ggml.dll');@(_LlamaCuda12Anchor->'%(RootDir)%(Directory)ggml-base.dll');@(_LlamaCuda12Anchor->'%(RootDir)%(Directory)mtmd.dll')"
          Exclude="@(ResolvedFileToPublish)" />
      <ResolvedFileToPublish Include="@(_LlamaCuda12Restored)">
        <RelativePath>runtimes/win-x64/native/cuda12/%(Filename)%(Extension)</RelativePath>
        <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
      </ResolvedFileToPublish>

      <ResolvedFileToPublish Include="@(_LlamaAvx2CpuAnchor->'%(FullPath)')">
        <RelativePath>runtimes/win-x64/native/cuda12/ggml-cpu.dll</RelativePath>
        <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
      </ResolvedFileToPublish>
    </ItemGroup>
  </Target>
```

- [ ] **Step 7: Verify the publish end-to-end with the guard**

```powershell
$out = "$env:TEMP\ls-assistant-publish-check"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
dotnet publish F:\LocalScribe\src\LocalScribe.Assistant -c Release -r win-x64 --self-contained -o $out
pwsh F:\LocalScribe\tools\verify-assistant-publish.ps1 -PublishDir $out
```

Expected: publish succeeds (no NETSDK1152), script prints `PASS: assistant publish layout complete (27 required files present).` and exits 0.
Then negative-check the guard: `Remove-Item $out\runtimes\win-x64\native\cuda12\llama.dll; pwsh F:\LocalScribe\tools\verify-assistant-publish.ps1 -PublishDir $out` → prints FAIL listing that file, exit code 1.

- [ ] **Step 8: Commit**

```powershell
git add src/LocalScribe.Assistant/LocalScribe.Assistant.csproj src/LocalScribe.Core/Assistant/AssistantPublishLayout.cs tools/verify-assistant-publish.ps1 tests/LocalScribe.Core.Tests/AssistantPublishLayoutTests.cs
git commit -m "feat(assistant): folder-publish layout - ungated target restores cuda12 set + avx2 ggml-cpu, guard script"
```

---

### Task 4: Helper honesty — explicit CUDA selection, offload-parsed backend, fall event

**Files:**
- Modify: `src/LocalScribe.Assistant/LlamaEngine.cs` (Load + native config + ChatML comment)
- Modify: `src/LocalScribe.Assistant/Program.cs` (remove experiment lines; keep a permanent stderr exception dump)

**Interfaces:**
- Consumes: `LlamaOffloadLog.FindOffload/IsFullGpu`, `AssistantWire.CudaFellPhase` (Task 1).
- Produces: unchanged wire contract. Behavior (design section 5 table): `auto` full-GPU → `backend=cuda`; `auto` otherwise → progress event `cuda-fell-to-cpu` then `backend=cpu`; explicit `cuda` not-full-GPU → throw (JOB_FAILED); `cpu` → `backend=cpu`. No reload on a fall — only the label changes.

This is the humble object at the native boundary (existing rule: not unit-tested; wire pinned by AssistantJobRunnerTests/ProcessAssistantHelperTests; real-model path smoke-only). The decision logic lives in Core (Task 1, unit-tested); this task wires it.

- [ ] **Step 1: Rewrite `LlamaEngine.cs`**

Full new content (replaces the whole file):

```csharp
// Humble object at the LLamaSharp/native boundary (the SherpaDiarisationRunner precedent):
// not unit-tested; the stdio contract around it is pinned by AssistantJobRunnerTests and
// ProcessAssistantHelperTests, the offload-parse + backend rule is unit-tested in Core
// (LlamaOffloadLogTests, against real captured llama.cpp logs), and the real-model path is
// smoke-only (runbook section B).
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Native;
using LocalScribe.Core.Assistant;

namespace LocalScribe.Assistant;

internal sealed class LlamaEngine : IDisposable
{
    /// <summary>The backend ACTUALLY used ("cuda" or "cpu") - reported in every done event
    /// (floor-fall provenance, design 7.7: CUDA fall to CPU is recorded, never silent).
    /// "cuda" is asserted ONLY when llama.cpp's own load_tensors log reports every layer
    /// offloaded (design 2026-07-23 section 5) - LLamaWeights.LoadFromFile not throwing
    /// proves nothing (llama.cpp silently assigns all layers to CPU when no CUDA backend
    /// is registered; three real runs shipped as "cuda" that way).</summary>
    public string Backend { get; private set; }
    public int LastPromptTokens { get; private set; }

    private static readonly object LogLock = new();
    private static readonly StringBuilder LoadLog = new();
    private static bool _nativeConfigured;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;

    /// <summary>Backend pick (design 7.1 + 2026-07-23 section 5): "cpu" -> CPU;
    /// "cuda"/"auto" -> load with full offload requested, then read the TRUTH from
    /// llama.cpp's load_tensors log: full offload -> "cuda"; anything else -> "cuda"
    /// throws (the documented GPU-or-throw contract), "auto" emits the cuda-fell-to-cpu
    /// progress event and reports "cpu". The loaded context is kept on a fall - only the
    /// label was wrong, reloading would waste the 13s model load.</summary>
    public static LlamaEngine Load(string modelPath, int ctxTokens, string backendRequest, Action<string> phase)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"model file missing: {modelPath}", modelPath);
        ConfigureNativeLoad(backendRequest);
        lock (LogLock) LoadLog.Clear();   // per-load reset (design 2026-07-23 section 5)
        if (backendRequest != "cpu")
        {
            LlamaEngine? engine = null;
            try
            {
                phase("load-cuda");
                engine = new LlamaEngine(modelPath, ctxTokens, gpuLayerCount: int.MaxValue, "cuda");
                string log;
                lock (LogLock) log = LoadLog.ToString();
                var offload = LlamaOffloadLog.FindOffload(log);
                if (LlamaOffloadLog.IsFullGpu(offload)) return engine;

                // Not a GPU run: the offload line is absent (no CUDA backend engaged) or
                // partial (VRAM pressure). A mixed run is NOT a GPU run (design section 5).
                if (backendRequest == "cuda")
                {
                    engine.Dispose();
                    throw new InvalidOperationException("CUDA was requested but the model did not fully "
                        + "offload to the GPU" + (offload is { } p
                            ? $" ({p.Offloaded}/{p.Total} layers offloaded)."
                            : " (no CUDA backend engaged - is the cuda12 native set deployed and an NVIDIA driver present?)."));
                }
                phase(AssistantWire.CudaFellPhase);   // recorded, never silent (design 7.7)
                engine.Backend = "cpu";
                return engine;
            }
            catch (Exception) when (backendRequest == "auto" && engine is null)
            {
                // The LOAD itself failed (e.g. a broken cuda12 deployment). CPU may still work
                // if the failure did not poison NativeApi's type initializer; if it did, the
                // retry below throws too and the job fails VISIBLY (JOB_FAILED on the wire).
                phase(AssistantWire.CudaFellPhase);
            }
        }
        phase("load-cpu");
        return new LlamaEngine(modelPath, ctxTokens, gpuLayerCount: 0, "cpu");
    }

    /// <summary>Once per process, BEFORE the first NativeApi touch. Two jobs:
    /// (1) capture llama.cpp's native log into the per-load buffer that backs the offload
    /// parse - echoed to STDERR and NEVER stdout, which is the wire (design section 5;
    /// the callback fires from native threads, hence the lock);
    /// (2) point LLamaSharp at the cuda12 llama.dll explicitly when the request wants GPU
    /// and an NVIDIA driver is present. LLamaSharp 0.25's own CUDA detection reads
    /// CUDA_PATH + version.json - the CUDA TOOLKIT, which end-user boxes never have - so
    /// its default policy NEVER selects cuda12 in the field (verified 2026-07-23; its
    /// SkipCheck escape hatch throws when fallback is enabled). Driver presence + full
    /// offload still decide the TRUTH above; pointing at the CUDA build with no usable GPU
    /// just yields zero offloaded layers, which parses as a fall. On a CPU request the
    /// default policy is left alone - it picks the CPU variant by CPU detection (verified:
    /// avx2 on an AVX2 box).</summary>
    private static void ConfigureNativeLoad(string backendRequest)
    {
        if (_nativeConfigured) return;
        _nativeConfigured = true;
        NativeLibraryConfig.All.WithLogCallback((level, msg) =>
        {
            lock (LogLock) LoadLog.Append(msg);
            Console.Error.Write(msg);
        });
        if (backendRequest == "cpu") return;
        string cudaLlama = Path.Combine(AppContext.BaseDirectory,
            "runtimes", "win-x64", "native", "cuda12", "llama.dll");
        if (File.Exists(cudaLlama) && NvidiaDriverPresent())
            NativeLibraryConfig.LLama.WithLibrary(cudaLlama);
    }

    private static bool NvidiaDriverPresent()
    {
        if (!NativeLibrary.TryLoad("nvcuda.dll", out nint driver)) return false;
        NativeLibrary.Free(driver);
        return true;
    }

    private LlamaEngine(string modelPath, int ctxTokens, int gpuLayerCount, string backend)
    {
        var p = new ModelParams(modelPath)
        {
            ContextSize = (uint)Math.Max(ctxTokens, 2048),   // per-job num_ctx (design 7.2)
            GpuLayerCount = gpuLayerCount,
            TypeK = GGMLType.GGML_TYPE_Q8_0,                 // KV cache q8_0 (design 7.2)
            TypeV = GGMLType.GGML_TYPE_Q8_0,
            FlashAttention = true,                           // required for the quantized V cache
        };
        _weights = LLamaWeights.LoadFromFile(p);
        _context = _weights.CreateContext(p);
        // InteractiveExecutor keeps the KV state across InferAsync calls - the warm-chat
        // prefix-reuse mechanism (design 7.1): the warmup prefills once, answers append.
        _executor = new InteractiveExecutor(_context);
        Backend = backend;
    }

    public static (string Prompt, int MaxTokens) ReadPayload(string payloadJson)
    {
        var o = JsonNode.Parse(payloadJson)!.AsObject();
        return (o["prompt"]?.GetValue<string>()
                    ?? throw new InvalidDataException("payload has no prompt"),
                o["maxTokens"]?.GetValue<int>() ?? 1024);
    }

    public async IAsyncEnumerable<string> InferAsync(string prompt, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ChatML template, correct for the LOCKED default model ONLY (Qwen3-4B-Instruct-2507,
        // a NON-thinking ChatML model - the whole budget goes to the answer, design 7.2).
        // Other models need their own wrapper: Qwen3-1.7B (non-Instruct-2507) THINKS - it
        // burns the entire token budget inside <think> and returns nothing; Gemma expects
        // <start_of_turn>, not ChatML (both verified on real weights 2026-07-23, and both
        // were REMOVED from the manifest for exactly this reason - design 2026-07-23
        // section 6). If a second model is ever wanted: per-model template metadata in
        // assistant-manifest.json, selected here - deliberately deferred as YAGNI.
        string wrapped = "<|im_start|>user\n" + prompt + "<|im_end|>\n<|im_start|>assistant\n";
        LastPromptTokens = _context.Tokenize(wrapped).Length;
        var ip = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = ["<|im_end|>"],
        };
        await foreach (string piece in _executor.InferAsync(wrapped, ip, ct))
            yield return piece;
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
    }
}
```

- [ ] **Step 2: Restore `Program.cs` to its committed shape plus one permanent stderr dump**

Remove the experiment block (the `using LLama.Native;`, the `NativeLibraryConfig...` lines and the `cudaLlama` probe added during design verification — native config now lives in `LlamaEngine.ConfigureNativeLoad`). In the job `catch`, keep a full-chain stderr dump (this is what made the deployment defect diagnosable; stderr is not the wire):

```csharp
        catch (Exception ex)
        {
            // Full chain to STDERR (never stdout - that is the wire): native-load failures
            // surface as a bare TypeInitializationException on Message alone; the inner
            // chain is what actually names the missing DLL (2026-07-23 diagnosis).
            Console.Error.WriteLine(ex.ToString());
            Emit(new AssistantError("JOB_FAILED: " + ex.Message));
            return 1;
        }
```

The file is otherwise byte-identical to its committed state (UTF-8 pinning, Emit, loop, keepAlive semantics all unchanged).

- [ ] **Step 3: Build clean + existing wire tests still green**

Run: `dotnet build F:\LocalScribe\LocalScribe.slnx 2>&1 | Select-String "warn|error"` → expect none.
Run: `dotnet test tests/LocalScribe.Core.Tests --filter "AssistantJobRunner|AssistantWire"` → expect PASS (wire contract untouched).

- [ ] **Step 4: Real-model smoke (this box: GTX 1650, driver-only)**

```powershell
$out = "$env:TEMP\ls-assistant-publish-check"   # from Task 3; republish if deleted
dotnet publish F:\LocalScribe\src\LocalScribe.Assistant -c Release -r win-x64 --self-contained -o $out
pwsh F:\LocalScribe\tools\verify-assistant-publish.ps1 -PublishDir $out
$m = 'F:\LocalScribe\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
& C:\temp\localscribe-assistant-handoff\ask-helper.ps1 -Exe "$out\LocalScribe.Assistant.exe" -ModelPath $m -Backend cuda -MaxTokens 32 -StderrFile "$env:TEMP\smoke-cuda.txt"
& C:\temp\localscribe-assistant-handoff\ask-helper.ps1 -Exe "$out\LocalScribe.Assistant.exe" -ModelPath $m -Backend cpu  -MaxTokens 32 -StderrFile "$env:TEMP\smoke-cpu.txt"
& C:\temp\localscribe-assistant-handoff\ask-helper.ps1 -Exe "$out\LocalScribe.Assistant.exe" -ModelPath $m -Backend auto -MaxTokens 32 -StderrFile "$env:TEMP\smoke-auto.txt"
Select-String -Path "$env:TEMP\smoke-cuda.txt" -Pattern 'offloaded \d+/\d+ layers'
```

Expected:
- `cuda`: `[done] backend=cuda`, stderr contains `offloaded 37/37 layers to GPU`, elapsed well under a minute (GPU-class, ~7s warm).
- `cpu`: `[done] backend=cpu`, no offload line, elapsed ~30s warm (avx2-class; minutes = noavx = layout regression).
- `auto`: `[done] backend=cuda` on this box.
- Fall path (simulated CPU-only box): `Rename-Item $out\runtimes\win-x64\native\cuda12 cuda12-off`, run `-Backend auto` → expect a `[progress] cuda-fell-to-cpu` line and `[done] backend=cpu`; run `-Backend cuda` → expect `[ERROR] JOB_FAILED: CUDA was requested but...`. Rename back afterwards.

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.Assistant/LlamaEngine.cs src/LocalScribe.Assistant/Program.cs
git commit -m "fix(assistant): explicit cuda12 selection + offload-parsed backend truth + recorded cuda-fell-to-cpu"
```

---

### Task 5: Availability = model AND helper (locator wiring, explainers, Settings card)

**Files:**
- Modify: `src/LocalScribe.App/CompositionRoot.cs:133-139` (locator + comment)
- Modify: `src/LocalScribe.App/App.xaml.cs:253-257` (chat factory helper gate)
- Modify: `src/LocalScribe.App/ViewModels/AssistantTabViewModel.cs` (probe + availability + explainer)
- Modify: `src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs:22-23` (UnavailableText reword)
- Modify: `src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs` (AssistantHelperNote + probe param)
- Modify: `src/LocalScribe.App/SettingsPage.xaml` (~line 166: helper note TextBlock)
- Test: `tests/LocalScribe.App.Tests/AssistantTabViewModelTests.cs` (extend), Settings VM test file (grep `class SettingsPageViewModelTests` under `tests/LocalScribe.App.Tests/`)

**Interfaces:**
- Consumes: `AssistantHelperLocator.FindExe()` / `.MissingMessage` / `.ExeName` (Task 2).
- Produces: `AssistantTabViewModel` ctor gains trailing `Func<string?>? helperProbe = null`; `SettingsPageViewModel` ctor gains trailing `Func<string?>? assistantHelperProbe = null` + `string AssistantHelperNote` observable.

- [ ] **Step 1: Write the failing tests**

Extend `AssistantTabViewModelTests.MakeVm` with `bool helper = true`, pass `helperProbe: () => helper ? @"C:\app\assistant\LocalScribe.Assistant.exe" : null` as the new trailing ctor argument. Add:

```csharp
    [Fact]
    public async Task Availability_needs_model_AND_helper_and_explainers_do_not_hide_each_other()
    {
        // Design 2026-07-23 section 4: a missing helper used to be indistinguishable from a
        // working one until the first request failed - exactly how the broken exe shipped.
        var noHelper = MakeVm(helper: false);
        await noHelper.LoadAsync("s1", CancellationToken.None);
        Assert.False(noHelper.AssistantAvailable);
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", noHelper.DisabledExplainer);
        Assert.DoesNotContain("No assistant model", noHelper.DisabledExplainer);

        var neither = MakeVm(anyModel: false, helper: false);
        await neither.LoadAsync("s1", CancellationToken.None);
        Assert.False(neither.AssistantAvailable);
        Assert.Contains("No assistant model", neither.DisabledExplainer);       // both shown -
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", neither.DisabledExplainer); // one fix
                                                                                 // must not hide the other
        var both = MakeVm();
        await both.LoadAsync("s1", CancellationToken.None);
        Assert.True(both.AssistantAvailable);
        Assert.Equal("", both.DisabledExplainer);
    }
```

In the Settings VM test file (mirror its existing construction helper; it already passes `modelsRoot`/`assistantModels` optionals), add:

```csharp
    [Fact]
    public void Assistant_helper_note_reports_present_and_absent_separately_from_models()
    {
        var present = MakeVm(assistantHelperProbe: () => @"C:\app\assistant\LocalScribe.Assistant.exe");
        Assert.Contains(@"C:\app\assistant\LocalScribe.Assistant.exe", present.AssistantHelperNote);

        var absent = MakeVm(assistantHelperProbe: () => null);
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", absent.AssistantHelperNote);
    }
```

(Adapt `MakeVm` to forward the new optional parameter; leave every existing test compiling by giving it a default.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "AssistantTabViewModelTests|SettingsPageViewModel"`
Expected: compile FAILURE — the new ctor parameters do not exist.

- [ ] **Step 3: Implement**

`AssistantTabViewModel`: add field + ctor param (trailing, after `dispatch`):

```csharp
    private readonly Func<string?> _helperProbe;
    // ctor signature gains: Func<string?>? helperProbe = null
    // ctor body gains:      _helperProbe = helperProbe ?? AssistantHelperLocator.FindExe;
```

Replace the availability block inside `LoadAsync`'s `_dispatch`:

```csharp
                string? helper = _helperProbe();
                AssistantAvailable = enabled && manifest is { Installed.Count: > 0 } && helper is not null;
                // Design 2026-07-23 section 4: model and helper are DISTINCT failures; when both
                // are missing both explainers show, so fixing one cannot hide the other.
                DisabledExplainer = !enabled
                    ? "The assistant is turned off in Settings."
                    : string.Join(" ", new[]
                      {
                          manifest is { Installed.Count: 0 }
                              ? "No assistant model is installed - see Settings > Assistant for fetch instructions."
                              : null,
                          helper is null ? AssistantHelperLocator.MissingMessage : null,
                      }.Where(s => s is not null));
```

`SettingsPageViewModel`: ctor gains trailing `Func<string?>? assistantHelperProbe = null`; add near the other assistant members:

```csharp
    /// <summary>Helper-present/absent, reported SEPARATELY from model-installed (design
    /// 2026-07-23 section 7) - a missing helper was previously invisible until first use.</summary>
    [ObservableProperty] private string _assistantHelperNote = "";
```

and at the end of the ctor (before `AssistantModelsLoad = ...` is fine; the probe is a cheap `File.Exists` chain):

```csharp
        AssistantHelperNote = (assistantHelperProbe ?? AssistantHelperLocator.FindExe)() is string helperPath
            ? $"Assistant helper: {helperPath}"
            : AssistantHelperLocator.MissingMessage;
```

`SettingsPage.xaml` — directly below the `AssistantModelsNote` TextBlock (~line 166), same styling:

```xml
                    <TextBlock Text="{Binding AssistantHelperNote, Mode=OneWay}"
                               TextWrapping="Wrap" Margin="0,4,0,0" Opacity="0.8" />
```

(match the sibling note's exact attributes — copy its `FontSize`/`Foreground`/`Margin` pattern if it differs from this sketch.)

`AssistantChatViewModel.UnavailableText` (chat cannot distinguish causes — its factory returns null for either):

```csharp
    /// <summary>Section 7.6 + 2026-07-23 section 4: assistant chat is disabled until BOTH a
    /// model and the deployed helper exist; Settings > Assistant names which one is missing.</summary>
    public const string UnavailableText =
        "The assistant is not available - see Settings > Assistant for model and helper status.";
```

(grep tests for the old literal `"No assistant model is installed. See Settings"` and update any assertion to the new const.)

`App.xaml.cs:253` — gate the QA scope factory on the helper too:

```csharp
        Func<LocalScribe.Core.Assistant.QaScopeFactory?> qaScopeFactoryFor = () =>
            assistantManifest?.DefaultModel is LocalScribe.Core.Assistant.AssistantModelInfo m
                && LocalScribe.Core.Assistant.AssistantHelperLocator.FindExe() is not null
                ? new LocalScribe.Core.Assistant.QaScopeFactory(
                    m.FilePath, System.IO.Path.GetFileName(m.FilePath), "auto", q => searchIndex.Query(q))
                : null;
```

`CompositionRoot.cs:133-139` — replace the stale comment + path:

```csharp
        // Local assistant (design 2026-07-18 section 7; deployment revised 2026-07-23): an
        // out-of-process LLamaSharp helper published as a FOLDER into an assistant\ subfolder -
        // deliberately NOT single-file like Diarizer, because LLamaSharp probes its
        // runtimes/<rid>/native/<variant>/ layout relative to the helper's own directory
        // (single-file self-extract lands the natives where that probe never looks; every
        // request then failed at NativeApi init, which is how the first deployment shipped
        // broken). The subfolder keeps the helper's own onnxruntime.dll isolated from the
        // App's - the same isolation goal as Diarizer's single-file rule, reached by a
        // different means. Resolution via AssistantHelperLocator (env override -> assistant\
        // subfolder -> repo tools\assistant dev fallback); when absent the UI disables the
        // assistant with the locator's MissingMessage (availability = model AND helper) and
        // this fallback path simply fails visibly if a job is somehow still attempted.
        // AssistantGate probes the SAME recording-busy condition RetranscriptionRunner uses
        // (above): assistant jobs yield to recording, visibly queued; recording is NEVER
        // gated by the assistant.
        string assistantExe = AssistantHelperLocator.FindExe()
            ?? Path.Combine(AppContext.BaseDirectory, "assistant", AssistantHelperLocator.ExeName);
```

(add `using LocalScribe.Core.Assistant;` if not present.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests`
Expected: all PASS (including the two new tests; the known `Stop_upserts...` flake excepted).

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.App tests/LocalScribe.App.Tests
git commit -m "feat(assistant): availability = model AND helper - locator wiring, distinct explainers, Settings helper note"
```

---

### Task 6: Fall provenance — persisted flag + UI line

**Files:**
- Modify: `src/LocalScribe.Core/Assistant/SummaryStore.cs` (SummaryVersion additive param)
- Modify: `src/LocalScribe.Core/Assistant/SummarizationService.cs` (capture the fall)
- Modify: `src/LocalScribe.App/ViewModels/AssistantTabViewModel.cs` (VersionInfo + live phase text)
- Test: extend `tests/LocalScribe.Core.Tests/SummarizationServiceTests.cs` and `tests/LocalScribe.App.Tests/AssistantTabViewModelTests.cs`

**Interfaces:**
- Consumes: `AssistantWire.CudaFellPhase` (Task 1).
- Produces: `SummaryVersion` gains trailing `bool CudaFellToCpu = false` (ADDITIVE: old sidecars deserialize to false; every existing positional construction still compiles). matter-qa reads summaries — additive is safe.

- [ ] **Step 1: Write the failing tests**

In `SummarizationServiceTests` (mirror its existing fake-runner construction pattern):

```csharp
    [Fact]
    public async Task Cuda_fall_progress_event_is_persisted_on_the_version()
    {
        // Design 2026-07-23 sections 5/7: the fall is recorded provenance, not just a
        // transient progress line.
        var vm = /* service built with a FakeRunner scripted as: */
        // [new AssistantProgress(AssistantWire.CudaFellPhase, 0, 0),
        //  new AssistantChunk("## S"), new AssistantDone("cpu", 5, 3)]
        var version = await service.SummarizeAsync("s1", null, null, CancellationToken.None);
        Assert.True(version.CudaFellToCpu);
        Assert.Equal("cpu", version.Model.Backend);

        // and the no-fall path stays false:
        // FakeRunner scripted [chunk, AssistantDone("cuda", 5, 3)] -> CudaFellToCpu false
    }
```

(write it as two facts using the file's existing service-construction helper; the comment shows the two scripts.)

In `AssistantTabViewModelTests`:

```csharp
    [Fact]
    public async Task Fall_is_stated_on_the_provenance_line_and_live_phase_is_friendly()
    {
        var vm = MakeVm(script: _ =>
            [new AssistantProgress(AssistantWire.CudaFellPhase, 0, 0),
             new AssistantChunk("## S"), new AssistantDone("cpu", 5, 3)]);
        await vm.LoadAsync("s1", CancellationToken.None);
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Contains("CPU", vm.VersionInfo);
        Assert.Contains("fell to CPU", vm.VersionInfo);      // stated explicitly (design section 7)

        var noFall = MakeVm();   // default script reports plain cpu, no fall event
        await noFall.LoadAsync("s1", CancellationToken.None);
        await noFall.RegenerateCommand.ExecuteAsync(null);
        Assert.DoesNotContain("fell to CPU", noFall.VersionInfo);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SummarizationService` and `dotnet test tests/LocalScribe.App.Tests --filter AssistantTabViewModelTests`
Expected: compile FAILURE — `CudaFellToCpu` does not exist.

- [ ] **Step 3: Implement**

`SummaryStore.cs` — extend the record (additive trailing optional; update the doc comment):

```csharp
/// <summary>One generated summary (design 2026-07-18 section 7.3 sidecar shape, LOCKED
/// contract - feat/matter-qa reads these for matter-scope context; CudaFellToCpu is a
/// 2026-07-23 ADDITIVE field, absent in older sidecars = false). SourceTranscriptVersion
/// stores SessionRecord.ActiveVersion / LoadedProjection.VersionId at generation time
/// ("v1" or a TranscriptVersion.Id). Model records the file + pinned sha256 + the backend
/// ACTUALLY used (from AssistantDone); CudaFellToCpu records that an "auto" request wanted
/// the GPU and could not fully offload (the recorded, never-silent fall - design 7.7).</summary>
public sealed record SummaryVersion(string Id, DateTimeOffset CreatedAt, string SourceTranscriptVersion,
    AssistantModelRef Model, int PromptVersion, string ContentMarkdown, bool Stale,
    bool CudaFellToCpu = false);
```

`SummarizationService.SummarizeAsync` — wrap the event sink once, right after the model pick (before `_loadProjection`):

```csharp
            // The helper's cuda-fell-to-cpu progress event (design 2026-07-23 section 5) is
            // provenance, not just UI: any fall across the job chain (map-reduce spawns one
            // helper per chunk) marks the whole version.
            bool cudaFell = false;
            Action<AssistantEvent> watchEvents = evt =>
            {
                if (evt is AssistantProgress p && p.Phase == AssistantWire.CudaFellPhase) cudaFell = true;
                onEvent?.Invoke(evt);
            };
```

then pass `watchEvents` (not `onEvent`) to both `RunJobAsync(...)` calls and `MapReduceAsync(...)`, and stamp the version: `Stale: false, CudaFellToCpu: cudaFell);`

`AssistantTabViewModel.OnSelectedVersionChanged` — backend segment of `VersionInfo` becomes:

```csharp
            $"{value.Id} · {value.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {value.Model.File} ({value.Model.Backend.ToUpperInvariant()}{(value.CudaFellToCpu ? " - GPU unavailable, fell to CPU" : "")}) · transcript {value.SourceTranscriptVersion}");
```

`AssistantTabViewModel.OnJobEvent` — friendly live line:

```csharp
            case AssistantProgress p:
                PhaseText = p.Phase == AssistantWire.CudaFellPhase
                    ? "GPU unavailable - continuing on CPU"
                    : p.Total > 0 ? $"{p.Phase} {p.Current}/{p.Total}" : p.Phase;
                WaitingText = "";
                break;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests` and `dotnet test tests/LocalScribe.App.Tests`
Expected: PASS (the 2 known Core fixture fails / 1 App flake excepted).

- [ ] **Step 5: Commit**

```powershell
git add src/LocalScribe.Core/Assistant src/LocalScribe.App/ViewModels/AssistantTabViewModel.cs tests/
git commit -m "feat(assistant): persist cuda-fell-to-cpu on SummaryVersion + state it on the provenance line"
```

---

### Task 7: Models + docs — drop 1.7B/Gemma, rewrite publish/deploy + section B

**Files:**
- Modify: `tools/fetch-models.ps1` (remove `-AssistantOptional`, both optional entries, add a merge drop-list)
- Modify: `docs/plans/2026-07-19-llm-foundation-summaries-smoke-runbook.md` (prereqs, publish step 2, section B)
- Local only (gitignored, no commit): `models/assistant-manifest.json` trimmed to the 4B entry

**Interfaces:** none new. Spec section 6: both optional models are dropped from script, manifest and picker (the picker follows the manifest automatically); downloaded weights are NOT deleted.

- [ ] **Step 1: fetch-models.ps1**

- Delete the `[switch] $AssistantOptional` param and its comment block; the gate becomes `if ($Assistant) {`.
- Delete the `Qwen3-1.7B-Instruct` and `Gemma-4-E2B-QAT` entries (and the Gemma license comment block) from `$assistantModels`; drop the now-constant `Optional` key and the `if ($m.Optional -and -not $AssistantOptional) { continue }` line.
- Update the header comment for the whole assistant section:

```powershell
# --- Assistant LLM (GGUF, design 2026-07-18 section 7.2; 2026-07-23: single-model) ---------
# ONLY the LOCKED default is fetched. The former optional entries (Qwen3-1.7B, Gemma-4-E2B)
# were REMOVED 2026-07-23: LlamaEngine hardcodes the ChatML non-thinking wrapper, which is
# correct for Qwen3-4B-Instruct-2507 alone - the 1.7B is a THINKING model (burns the whole
# budget in <think>, returns nothing) and Gemma is not ChatML (<start_of_turn>), both
# verified on real weights. If a second model is ever wanted: per-model template metadata in
# the manifest, selected by the engine (deferred as YAGNI).
```

- In the manifest merge loop, filter the dropped models so previously-provisioned boxes shed them on the next run (weights on disk stay - the script never deletes):

```powershell
        $droppedModels = @('Qwen3-1.7B-Q4_K_M.gguf', 'gemma-4-E2B_q4_0-it.gguf')
        foreach ($e in $existing) {
            if ($droppedModels -contains $e.file) { continue }   # 2026-07-23: engine cannot prompt these
            if (($manifestEntries | Where-Object { $_.file -eq $e.file }).Count -eq 0 -and
                ...unchanged...
```

- [ ] **Step 2: Trim the LOCAL manifest (this box has all three entries)**

Edit `F:\LocalScribe\models\assistant-manifest.json` to keep only the `Qwen3-4B-Instruct-2507` entry (schemaVersion 1, models array of one). Do NOT delete the two GGUF files (4.3 GB) — deleting downloaded weights is the user's call; the runbook notes it as optional cleanup.

- [ ] **Step 3: Runbook rewrite**

In `docs/plans/2026-07-19-llm-foundation-summaries-smoke-runbook.md`:

- **Prerequisites step 1:** delete the `-AssistantOptional` command + the paragraph about the two optional models; in the URL-corrections blockquote keep the Default-4B bullet, delete the 1.7B and Gemma bullets and the Gemma-license note; add one line: `Optional models removed 2026-07-23: the engine's ChatML non-thinking wrapper is only correct for the locked default (1.7B thinks; Gemma is not ChatML). Already-downloaded optional GGUFs are inert and may be deleted by hand if the disk space matters.`
- **Step 2 (publish and deploy)** — replace the whole step, including the copy-only-the-exe paragraph, with:

````markdown
**2. Publish and deploy the helper (FOLDER publish into an assistant\ subfolder - revised 2026-07-23).**
```powershell
dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained `
    -o src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
pwsh tools/verify-assistant-publish.ps1 -PublishDir src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
```
Single-file is IMPOSSIBLE for this helper (unlike Diarizer): LLamaSharp probes its
runtimes/<rid>/native/<variant>/ layout relative to the helper's own directory; self-extract
lands the natives where that probe never looks, and every request fails at NativeApi init.
The assistant\ subfolder keeps the helper's own onnxruntime.dll isolated from the App's.
The guard script MUST pass before smoking: a silent layout regression ships noavx, which
turns a ~2-minute summary into one that does not finish in 10.
````

- **Section B** — replace the backend-honesty expectations with the concrete A/B:

```markdown
- On a CUDA box (NVIDIA driver present, ~2.6 GB VRAM free): provenance line says CUDA; the
  helper stderr (if captured) contains `load_tensors: offloaded 37/37 layers to GPU`.
  Baseline: ~80s for a 1,145-token summary (4B Q4_K_M), ~13s of that model load.
- On a CPU-only box (or GPU busy): a "GPU unavailable - continuing on CPU" phase appears
  while generating, the provenance line says CPU and states the fall explicitly
  ("fell to CPU"). Baseline: ~112s (avx2). MINUTES-long with no progress = the noavx
  layout regression - re-run tools/verify-assistant-publish.ps1.
- Settings > Assistant shows the helper path when deployed, and the publish command when not.
```

- [ ] **Step 4: Verify + commit**

Run: `pwsh -NoProfile -Command "& { . { param() }; Get-Command -Syntax pwsh > $null }; exit 0"` is unnecessary — instead syntax-check: `pwsh -NoProfile -Command "[void][ScriptBlock]::Create((Get-Content F:\LocalScribe\tools\fetch-models.ps1 -Raw)); 'fetch-models parses OK'"`
Expected: `fetch-models parses OK`.

```powershell
git add tools/fetch-models.ps1 docs/plans/2026-07-19-llm-foundation-summaries-smoke-runbook.md
git commit -m "feat(assistant): single-model manifest (drop 1.7B/Gemma) + folder-publish runbook with layout guard"
```

---

### Task 8: Final gate — full build/tests, deploy to App bin, end-to-end smoke

**Files:** none new (verification only)

- [ ] **Step 1: Full gate**

```powershell
dotnet build F:\LocalScribe\LocalScribe.slnx -warnaserror 2>&1 | Select-Object -Last 5
dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests
dotnet test F:\LocalScribe\tests\LocalScribe.App.Tests
```

Expected: build 0 warnings; Core green except the 2 known fixture fails; App green (1 known flake). If `LocalScribe.App.exe` is running, Core.dll copy fails with MSB3027 — that is a file lock, not a compile error; close the app.

- [ ] **Step 2: Deploy beside the App + guard + locator sanity**

```powershell
dotnet publish F:\LocalScribe\src\LocalScribe.Assistant -c Release -r win-x64 --self-contained `
    -o F:\LocalScribe\src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
pwsh F:\LocalScribe\tools\verify-assistant-publish.ps1 -PublishDir F:\LocalScribe\src\LocalScribe.App\bin\Debug\net10.0-windows\assistant
```

Expected: PASS, 27 files.

- [ ] **Step 3: End-to-end smoke through the DEPLOYED path**

```powershell
$exe = 'F:\LocalScribe\src\LocalScribe.App\bin\Debug\net10.0-windows\assistant\LocalScribe.Assistant.exe'
$m = 'F:\LocalScribe\models\Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
& C:\temp\localscribe-assistant-handoff\ask-helper.ps1 -Exe $exe -ModelPath $m -Backend auto -MaxTokens 48 -StderrFile "$env:TEMP\final-auto.txt"
Select-String -Path "$env:TEMP\final-auto.txt" -Pattern 'offloaded \d+/\d+ layers'
```

Expected on this box: `[done] backend=cuda`, `offloaded 37/37`, coherent one-sentence answer. GUI smoke (Assistant tab regenerate, Settings card) remains the USER's manual runbook.

- [ ] **Step 4: Wrap up**

Use superpowers:finishing-a-development-branch (merge choice is the user's; recent rounds merge `--no-ff` to master after a whole-branch review).

---

## Self-Review (done at plan time)

- **Spec coverage:** section 1 publish shape → T3 (amended per verified facts 3-6); section 2 layout → T3+T8; section 3 locator → T2+T5; section 4 availability → T5; section 5 provenance → T1+T4+T6; section 6 models → T7 (+engine comment in T4); section 7 UI → T5 (Settings) + T6 (provenance line); Testing section: parser fixtures T1, fall behaviour T4 (smoke) + T6 (service tests), locator T2, availability T5, layout guard T3, real-model smoke T4/T8 + runbook B for the user's CPU-only A/B half. Docs-to-update: runbook T7, CompositionRoot T5, csproj T3, LlamaEngine comment T4.
- **Known deltas from the spec, user-approved 2026-07-23:** (a) the csproj target must re-inject 4 conflict-dropped CUDA DLLs and co-locate avx2 ggml-cpu.dll (spec assumed preservation alone sufficed); (b) the helper explicitly selects cuda12\llama.dll via WithLibrary when GPU is wanted and a driver is present (spec assumed LLamaSharp's default probe would find CUDA); (c) cuda12 = 6 files in the guard, not 5.
- **Type consistency:** `LlamaOffloadLog.FindOffload/IsFullGpu`, `AssistantWire.CudaFellPhase`, `AssistantHelperLocator.FindExe/ExeName/MissingMessage`, `AssistantPublishLayout.RequiredRelativePaths/FindMissing`, `SummaryVersion.CudaFellToCpu` — used identically across tasks.

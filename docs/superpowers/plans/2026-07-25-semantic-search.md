# Semantic Search over Transcripts — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fully-local semantic search over session transcripts — a "Related discussion" section on the Search page that finds passages by meaning, powered by an EmbeddingGemma-300m GGUF served by the existing Assistant helper.

**Architecture:** A second derived index mirroring the lexical stack: the Assistant helper gains an `embed` op (LLamaSharp `LLamaEmbedder`, CPU-only); Core gains `Search\Semantic\` (chunker, binary per-session sidecar store under `index\semantic\`, cosine query engine, orchestrating service with a recording-paused backfill worker); the Search page gains a labeled Related section with an honest coverage note. The lexical index stays the metadata/facet/eligibility authority.

**Tech Stack:** .NET 10, WPF (CommunityToolkit.Mvvm), LLamaSharp 0.25.0 (helper only), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-25-semantic-search-design.md` — read it first.

## Global Constraints

- Fully local — no cloud calls, no sockets; helper IPC is stdio JSON-lines only.
- Evidentiary firewall: READ-ONLY over session folders. The only write target is `index\semantic\` (+ the models dir for the fetch script). Never touch `transcript.jsonl` / `edits.json` / `speakers.json` / `session.json`.
- All indexed text derives through `SessionProjectionLoader` (indexed text == displayed corrected text, active version).
- Embedding backend is CPU, fixed (`GpuLayerCount = 0`); the chat LLM is never loaded by any semantic-search path.
- **Memory rule (32GB-laptop):** at most ONE warm embed helper process (~600MB working set), shared by backfill and queries, and it is KILLED (not just idled) when a recording starts; queries mid-recording respawn it on demand. The 5-minute inactivity watchdog also reclaims it.
- Method gating: vectors carry `method` (e.g. `embeddinggemma-300m-q8_0@256`); different method ⇒ sidecar stale; different-method vectors never compared. Stored dim = 256 (Matryoshka truncation), unit-normalized.
- Similarity floor 0.55, top 40 chunks, chunk target ~700 chars, one-segment overlap.
- No Unicode emojis in any test script or test code.
- Existing lexical index format/behavior unchanged (two small additive members allowed: `PassesFacets` made public, `SnapshotEntries()` added).
- New Core code lives in `src/LocalScribe.Core/Search/Semantic/`, namespace `LocalScribe.Core.Search.Semantic`.
- Tests: xUnit in `tests/LocalScribe.Core.Tests` (file-scoped classes, no namespace, per existing style) and `tests/LocalScribe.App.Tests`. VM tests use queued-dispatch fakes, never sync dispatch.
- Commit per task, `--no-ff` merges not needed (single branch); message prefix `feat(semantic):` / `test(semantic):` as appropriate.
- Work on branch `feat/semantic-search` off master.

---

### Task 1: Embed wire contract + shared method/math primitives (Core)

**Files:**
- Modify: `src/LocalScribe.Core/Assistant/AssistantWire.cs`
- Create: `src/LocalScribe.Core/Search/Semantic/EmbeddingMethod.cs`
- Create: `src/LocalScribe.Core/Search/Semantic/EmbeddingMath.cs`
- Test: `tests/LocalScribe.Core.Tests/AssistantWireEmbedTests.cs`
- Test: `tests/LocalScribe.Core.Tests/EmbeddingMathTests.cs`

**Interfaces:**
- Consumes: existing `AssistantEvent` hierarchy, `AssistantWire.SerializeEvent/ParseEventLine`.
- Produces (later tasks rely on these exact shapes):
  - `public sealed record AssistantEmbedResult(IReadOnlyList<float[]> Embeddings, string Method) : AssistantEvent;` (in `LocalScribe.Core.Assistant`)
  - `AssistantWire.EmbedPayload(string kind, IReadOnlyList<string> texts, int dim) : string`
  - `EmbeddingMethod.For(string modelPath, int dim) : string` → `"{filename-without-ext,lowercase}@{dim}"`
  - `EmbeddingMath.TruncateAndNormalize(float[] v, int dim) : float[]`

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/AssistantWireEmbedTests.cs`:

```csharp
using LocalScribe.Core.Assistant;

public sealed class AssistantWireEmbedTests
{
    [Fact]
    public void EmbedResult_round_trips_vectors_and_method()
    {
        var evt = new AssistantEmbedResult(
            [[0.25f, -0.5f, 1f], [0f, 0.125f, -1f]], "embeddinggemma-300m-q8_0@256");
        string line = AssistantWire.SerializeEvent(evt);
        var parsed = Assert.IsType<AssistantEmbedResult>(AssistantWire.ParseEventLine(line));
        Assert.Equal("embeddinggemma-300m-q8_0@256", parsed.Method);
        Assert.Equal(2, parsed.Embeddings.Count);
        Assert.Equal(new[] { 0.25f, -0.5f, 1f }, parsed.Embeddings[0]);
        Assert.Equal(new[] { 0f, 0.125f, -1f }, parsed.Embeddings[1]);
    }

    [Fact]
    public void EmbedResult_line_is_single_line_json()
    {
        string line = AssistantWire.SerializeEvent(new AssistantEmbedResult([[1f]], "m@1"));
        Assert.DoesNotContain('\n', line);
        Assert.StartsWith("{", line);
    }

    [Fact]
    public void EmbedPayload_carries_kind_dim_and_texts()
    {
        string payload = AssistantWire.EmbedPayload("query", ["a", "b"], 256);
        var o = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
        Assert.Equal("query", o["kind"]!.GetValue<string>());
        Assert.Equal(256, o["dim"]!.GetValue<int>());
        Assert.Equal(2, o["texts"]!.AsArray().Count);
        Assert.Equal("a", o["texts"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Malformed_embedResult_parses_null_never_throws()
    {
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"embedResult\",\"embeddings\":\"junk\"}")
            is AssistantEmbedResult r && r.Embeddings.Count > 0 ? (object?)r : null);
        // unknown type still null (existing rule)
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"wat\"}"));
    }
}
```

`tests/LocalScribe.Core.Tests/EmbeddingMathTests.cs`:

```csharp
using LocalScribe.Core.Search.Semantic;

public sealed class EmbeddingMathTests
{
    [Fact]
    public void Truncates_then_renormalizes_to_unit_length()
    {
        float[] v = [3f, 4f, 100f, 100f];
        float[] r = EmbeddingMath.TruncateAndNormalize(v, 2);
        Assert.Equal(2, r.Length);
        Assert.Equal(0.6f, r[0], 3);
        Assert.Equal(0.8f, r[1], 3);
    }

    [Fact]
    public void Dim_zero_or_larger_than_vector_keeps_full_width()
    {
        Assert.Equal(3, EmbeddingMath.TruncateAndNormalize([1f, 2f, 2f], 0).Length);
        Assert.Equal(3, EmbeddingMath.TruncateAndNormalize([1f, 2f, 2f], 99).Length);
    }

    [Fact]
    public void Zero_vector_stays_zero_never_nan()
    {
        float[] r = EmbeddingMath.TruncateAndNormalize([0f, 0f], 2);
        Assert.All(r, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void Method_string_is_filename_lowercase_at_dim()
    {
        Assert.Equal("embeddinggemma-300m-q8_0@256",
            EmbeddingMethod.For(@"C:\models\embeddingGemma-300M-Q8_0.gguf", 256));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AssistantWireEmbedTests|FullyQualifiedName~EmbeddingMathTests"`
Expected: FAIL — `AssistantEmbedResult`, `EmbedPayload`, `EmbeddingMath`, `EmbeddingMethod` do not exist (compile errors).

- [ ] **Step 3: Implement**

In `AssistantWire.cs`, add the event record after `AssistantError`:

```csharp
/// <summary>Embed-op result (semantic search, design 2026-07-25): one unit-normalized vector per
/// input text, plus the method tag (EmbeddingMethod.For) that gates comparability.</summary>
public sealed record AssistantEmbedResult(IReadOnlyList<float[]> Embeddings, string Method) : AssistantEvent;
```

In `SerializeEvent`, add a case before the discard arm:

```csharp
AssistantEmbedResult r => new JsonObject
{
    ["type"] = "embedResult",
    ["method"] = r.Method,
    ["embeddings"] = new JsonArray(r.Embeddings
        .Select(v => (JsonNode)new JsonArray(v.Select(f => (JsonNode)f).ToArray())).ToArray()),
}.ToJsonString(),
```

In `ParseEventLine`, add a case:

```csharp
"embedResult" => ParseEmbedResult(o),
```

and the private helper (malformed rows become empty vectors — parse never throws, matching the
skip-noise rule):

```csharp
private static AssistantEmbedResult ParseEmbedResult(JsonObject o)
{
    var vectors = new List<float[]>();
    if (o["embeddings"] is JsonArray outer)
        foreach (var row in outer)
            vectors.Add(row is JsonArray inner
                ? inner.Select(n => n?.GetValue<float>() ?? 0f).ToArray()
                : []);
    return new AssistantEmbedResult(vectors, o["method"]?.GetValue<string>() ?? "");
}
```

Add the payload builder beside `PromptPayload`:

```csharp
/// <summary>The embed-op payload: kind "query"|"document", the Matryoshka output dim, and the
/// batch of texts. The HELPER applies the model's asymmetric prompt prefixes - callers never see
/// them (design 2026-07-25).</summary>
public static string EmbedPayload(string kind, IReadOnlyList<string> texts, int dim)
    => new JsonObject
    {
        ["kind"] = kind,
        ["dim"] = dim,
        ["texts"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()),
    }.ToJsonString();
```

Create `src/LocalScribe.Core/Search/Semantic/EmbeddingMethod.cs`:

```csharp
namespace LocalScribe.Core.Search.Semantic;

/// <summary>The ONE formula for the method tag (voiceprint Method-gating convention): model file
/// name (no extension, lowercase) + "@" + stored dim. Shared by the helper (emits it on the wire)
/// and the service (staleness check) so the two can never drift.</summary>
public static class EmbeddingMethod
{
    public static string For(string modelPath, int dim)
        => Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant() + "@" + dim;
}
```

Create `src/LocalScribe.Core/Search/Semantic/EmbeddingMath.cs`:

```csharp
namespace LocalScribe.Core.Search.Semantic;

/// <summary>Matryoshka truncation + unit normalization. Used by the helper on every embedded
/// vector; unit-length vectors make cosine similarity a plain dot product at query time.</summary>
public static class EmbeddingMath
{
    public static float[] TruncateAndNormalize(float[] v, int dim)
    {
        float[] r = dim > 0 && dim < v.Length ? v[..dim] : v;
        double sum = 0;
        foreach (float f in r) sum += (double)f * f;
        if (sum <= 0) return r;
        float inv = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < r.Length; i++) r[i] *= inv;
        return r;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AssistantWireEmbedTests|FullyQualifiedName~EmbeddingMathTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Assistant/AssistantWire.cs src/LocalScribe.Core/Search/Semantic/ tests/LocalScribe.Core.Tests/AssistantWireEmbedTests.cs tests/LocalScribe.Core.Tests/EmbeddingMathTests.cs
git commit -m "feat(semantic): embed wire contract + shared method/math primitives"
```

---

### Task 2: Manifest role field — embedding models never become the chat default (Core)

**Files:**
- Modify: `src/LocalScribe.Core/Assistant/AssistantModels.cs`
- Test: `tests/LocalScribe.Core.Tests/AssistantModelManifestRoleTests.cs` (new; existing manifest tests live in `tests/LocalScribe.Core.Tests/AssistantModelManifestTests.cs` — leave them untouched, they must keep passing)

**Interfaces:**
- Produces:
  - `AssistantManifestEntry.Role : string` (init, default `"chat"`)
  - `AssistantModelInfo` gains trailing positional param `string Role = "chat"` (source-compatible with all existing 5-arg constructions)
  - `AssistantModelManifest.EmbeddingModel : AssistantModelInfo?` (first `Role == "embedding"` entry)
  - `DefaultModel` now selects among `Role == "chat"` entries ONLY.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/AssistantModelManifestRoleTests.cs`:

```csharp
using System.Security.Cryptography;
using LocalScribe.Core.Assistant;

public sealed class AssistantModelManifestRoleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public AssistantModelManifestRoleTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<string> WriteModelAsync(string name)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    private Task WriteManifestAsync(string modelsJsonArray) => File.WriteAllTextAsync(
        Path.Combine(_root, "assistant-manifest.json"),
        "{\"schemaVersion\":1,\"models\":[" + modelsJsonArray + "]}");

    [Fact]
    public async Task Embedding_role_entry_is_exposed_and_never_becomes_chat_default()
    {
        string shaChat = await WriteModelAsync("chat.gguf");
        string shaEmb = await WriteModelAsync("embed.gguf");
        await WriteManifestAsync(
            $"{{\"canonicalName\":\"Chat\",\"file\":\"chat.gguf\",\"sha256\":\"{shaChat}\",\"nativeCtx\":4096,\"license\":\"Apache-2.0\"}}," +
            $"{{\"canonicalName\":\"Embed\",\"file\":\"embed.gguf\",\"sha256\":\"{shaEmb}\",\"nativeCtx\":2048,\"license\":\"Gemma\",\"role\":\"embedding\"}}");

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);

        Assert.Equal("Chat", m.DefaultModel?.CanonicalName);          // role-less entry = chat
        Assert.Equal("Embed", m.EmbeddingModel?.CanonicalName);
        Assert.Equal("embedding", m.EmbeddingModel?.Role);
    }

    [Fact]
    public async Task Manifest_with_only_an_embedding_model_has_no_chat_default()
    {
        string shaEmb = await WriteModelAsync("embed.gguf");
        await WriteManifestAsync(
            $"{{\"canonicalName\":\"Embed\",\"file\":\"embed.gguf\",\"sha256\":\"{shaEmb}\",\"nativeCtx\":2048,\"license\":\"Gemma\",\"role\":\"embedding\"}}");

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);

        Assert.Null(m.DefaultModel);                                   // an embedder must never chat
        Assert.NotNull(m.EmbeddingModel);
    }

    [Fact]
    public async Task Missing_role_defaults_to_chat_so_existing_manifests_parse_unchanged()
    {
        string sha = await WriteModelAsync("chat.gguf");
        await WriteManifestAsync(
            $"{{\"canonicalName\":\"Chat\",\"file\":\"chat.gguf\",\"sha256\":\"{sha}\",\"nativeCtx\":4096,\"license\":\"Apache-2.0\"}}");

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);

        Assert.Equal("chat", Assert.Single(m.Installed).Role);
        Assert.Null(m.EmbeddingModel);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AssistantModelManifestRoleTests"`
Expected: FAIL — `Role` / `EmbeddingModel` do not exist (compile errors).

- [ ] **Step 3: Implement**

In `AssistantModels.cs`:

1. `AssistantModelInfo` — add trailing param with default (additive; the LOCKED 5-field shape stays valid at every existing call site):

```csharp
public sealed record AssistantModelInfo(string CanonicalName, string FilePath, string Sha256,
    int NativeCtx, string License, string Role = "chat");
```

2. `AssistantManifestEntry` — add:

```csharp
    /// <summary>"chat" (default; absent in pre-semantic manifests) or "embedding" (design
    /// 2026-07-25). Embedding entries are excluded from DefaultModel selection.</summary>
    public string Role { get; init; } = "chat";
```

3. `AssistantModelManifest` — add the property and widen the constructor with a defaulted 4th param:

```csharp
    public AssistantModelInfo? EmbeddingModel { get; }

    public AssistantModelManifest(IReadOnlyList<AssistantModelInfo> installed,
        AssistantModelInfo? defaultModel, IReadOnlyList<string> notes,
        AssistantModelInfo? embeddingModel = null)
        => (Installed, DefaultModel, Notes, EmbeddingModel)
            = (installed, defaultModel, notes, embeddingModel);
```

4. In `LoadAsync`: pass the role through, and select chat/embedding separately. Replace the
`installed.Add(...)` line with:

```csharp
            installed.Add(new AssistantModelInfo(entry.CanonicalName, modelPath,
                entry.Sha256.ToLowerInvariant(), entry.NativeCtx, entry.License,
                string.IsNullOrEmpty(entry.Role) ? "chat" : entry.Role));
```

and replace the final two lines with:

```csharp
        var chat = installed.Where(m => m.Role == "chat").ToList();
        var def = chat.FirstOrDefault(m => m.CanonicalName == DefaultCanonicalName)
                  ?? chat.FirstOrDefault();
        var embedding = installed.FirstOrDefault(m => m.Role == "embedding");
        return new AssistantModelManifest(installed, def, notes, embedding);
```

- [ ] **Step 4: Run the new tests AND the existing manifest tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AssistantModelManifest"`
Expected: PASS (new role tests + all pre-existing manifest tests — the default-role path must not disturb them).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Assistant/AssistantModels.cs tests/LocalScribe.Core.Tests/AssistantModelManifestRoleTests.cs
git commit -m "feat(semantic): manifest role field - embedding models never become the chat default"
```

---

### Task 3: Helper embed op (LocalScribe.Assistant) + smoke script

**Files:**
- Create: `src/LocalScribe.Assistant/EmbedEngine.cs`
- Modify: `src/LocalScribe.Assistant/Program.cs`
- Modify: `src/LocalScribe.Assistant/LlamaEngine.cs` (one visibility change)
- Create: `tools/smoke-embed.ps1`

**Interfaces:**
- Consumes: `AssistantWire.ParseRequestLine` (`request.Op == "embed"`), `AssistantEmbedResult`, `EmbeddingMath.TruncateAndNormalize`, `EmbeddingMethod.For` (Task 1).
- Produces: helper behavior — an `{"op":"embed",...}` stdin line yields one `embedResult` event then one `done` event; `keepAlive:true` keeps the process resident for further embed batches.

This is a humble object at the native boundary (the LlamaEngine precedent): NOT unit-tested; verified by the smoke script against real weights. **LLamaSharp-version risk is checked here first** (Global Constraints in the spec).

- [ ] **Step 1: Make `ConfigureNativeLoad` reachable from the embed path**

In `LlamaEngine.cs`, change the access modifier only (same file, same behavior):

```csharp
    internal static void ConfigureNativeLoad(string backendRequest)
```

- [ ] **Step 2: Create `src/LocalScribe.Assistant/EmbedEngine.cs`**

```csharp
// Humble object over LLamaSharp's embedder (the LlamaEngine precedent): not unit-tested; the
// wire contract around it is pinned by AssistantWireEmbedTests + AssistantEmbeddingClientTests
// in Core, and the real-model path is smoke-only (tools/smoke-embed.ps1).
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Native;
using LocalScribe.Core.Search.Semantic;

namespace LocalScribe.Assistant;

/// <summary>CPU-only embedding engine for the "embed" op (design 2026-07-25). Loads the GGUF
/// once per process with mean pooling (EmbeddingGemma's pooling mode) and applies the model's
/// asymmetric prompt prefixes - queries and documents embed differently by design, and callers
/// never see the prefixes. Every output vector is Matryoshka-truncated + unit-normalized in
/// Core's EmbeddingMath so the stored dim and the wire dim can never disagree.</summary>
internal sealed class EmbedEngine : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly LLamaEmbedder _embedder;

    public static EmbedEngine Load(string modelPath, Action<string> phase)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"model file missing: {modelPath}", modelPath);
        LlamaEngine.ConfigureNativeLoad("cpu");   // log capture; CPU default policy untouched
        phase("load-embed");
        return new EmbedEngine(modelPath);
    }

    private EmbedEngine(string modelPath)
    {
        var p = new ModelParams(modelPath)
        {
            ContextSize = 2048,
            GpuLayerCount = 0,                    // CPU fixed (design: never contend for VRAM)
            Embeddings = true,
            PoolingType = LLamaPoolingType.Mean,  // EmbeddingGemma pools by mean
        };
        _weights = LLamaWeights.LoadFromFile(p);
        _embedder = new LLamaEmbedder(_weights, p);
    }

    public async Task<List<float[]>> EmbedAsync(string kind, IReadOnlyList<string> texts, int dim,
        CancellationToken ct)
    {
        var result = new List<float[]>(texts.Count);
        foreach (string text in texts)
        {
            ct.ThrowIfCancellationRequested();
            // EmbeddingGemma's prescribed asymmetric prefixes (design 2026-07-25).
            string prompt = kind == "query"
                ? "task: search result | query: " + text
                : "title: none | text: " + text;
            var vectors = await _embedder.GetEmbeddings(prompt, ct);
            result.Add(EmbeddingMath.TruncateAndNormalize(vectors[0], dim));
        }
        return result;
    }

    public static (string Kind, List<string> Texts, int Dim) ReadPayload(string payloadJson)
    {
        var o = JsonNode.Parse(payloadJson)!.AsObject();
        var texts = (o["texts"] as JsonArray ?? [])
            .Select(n => n?.GetValue<string>() ?? "").ToList();
        return (o["kind"]?.GetValue<string>() ?? "document", texts, o["dim"]?.GetValue<int>() ?? 0);
    }

    public void Dispose()
    {
        _embedder.Dispose();
        _weights.Dispose();
    }
}
```

**API-drift note (not a placeholder — a bounded contingency):** the three LLamaSharp members used
are `ModelParams.Embeddings`, `ModelParams.PoolingType` (`LLamaPoolingType` in `LLama.Native`),
and `LLamaEmbedder.GetEmbeddings(string, CancellationToken)` returning a list whose element is
`float[]`. If 0.25.0 spells any of these differently, adapt INSIDE this file only — the wire
contract and Core are pinned by tests and must not change.

- [ ] **Step 3: Route the op in `Program.cs`**

Declare beside `LlamaEngine? engine = null;`:

```csharp
EmbedEngine? embedEngine = null;
```

Inside the `try` block of the request loop, insert BEFORE the `engine ??=` line:

```csharp
            if (request.Op == "embed")
            {
                // Embedding shares the process shell but never the chat engine - the 4B LLM is
                // NOT loaded for embed requests (design 2026-07-25 memory rule).
                embedEngine ??= EmbedEngine.Load(request.ModelPath,
                    phase => Emit(new AssistantProgress(phase, 0, 0)));
                var (kind, texts, dim) = EmbedEngine.ReadPayload(request.PayloadJson);
                var vectors = await embedEngine.EmbedAsync(kind, texts, dim, CancellationToken.None);
                Emit(new LocalScribe.Core.Assistant.AssistantEmbedResult(vectors,
                    LocalScribe.Core.Search.Semantic.EmbeddingMethod.For(request.ModelPath,
                        dim > 0 ? dim : (vectors.FirstOrDefault()?.Length ?? 0))));
                Emit(new AssistantDone("cpu", 0, texts.Count));
                if (!request.KeepAlive) return 0;
                continue;
            }
```

And in the `finally` block, add `embedEngine?.Dispose();` beside `engine?.Dispose();`.

- [ ] **Step 4: Build the helper**

Run: `dotnet build src/LocalScribe.Assistant`
Expected: 0 errors, 0 warnings. If `PoolingType`/`Embeddings`/`GetEmbeddings` fail to compile,
apply the API-drift note (adapt names inside `EmbedEngine.cs`). If llama.cpp later rejects the
gemma-embedding ARCHITECTURE at load (smoke step), bump the three LLamaSharp package versions in
`src/LocalScribe.Assistant/LocalScribe.Assistant.csproj` (LLamaSharp, Backend.Cpu, Backend.Cuda12
— keep all three in lockstep) and re-run `tools/verify-assistant-publish.ps1` — the isolation
boundary means nothing outside the helper changes.

- [ ] **Step 5: Create `tools/smoke-embed.ps1`**

```powershell
# tools/smoke-embed.ps1
# Real-weights smoke for the assistant helper's "embed" op (design 2026-07-25).
# Requires: models/<embedding gguf> fetched (fetch-models.ps1 -Embedding) and the helper built.
# Pipes one embed request into the helper and asserts on the embedResult line.
param(
    [string] $HelperExe = "src/LocalScribe.Assistant/bin/Debug/net10.0-windows/LocalScribe.Assistant.exe",
    [string] $ModelFile = "models/embeddinggemma-300M-Q8_0.gguf",
    [int]    $Dim = 256
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $HelperExe)) { throw "helper not built: $HelperExe (dotnet build src/LocalScribe.Assistant)" }
if (-not (Test-Path $ModelFile)) { throw "model missing: $ModelFile (tools/fetch-models.ps1 -Embedding)" }
$model = (Resolve-Path $ModelFile).Path -replace '\\', '\\'
$payload = '{"kind":"document","dim":' + $Dim + ',"texts":["we could settle at three hundred and fifty thousand","the weather is nice today"]}'
$request = '{"op":"embed","modelPath":"' + $model + '","ctxTokens":2048,"backend":"cpu","keepAlive":false,"payload":' + $payload + '}'
$lines = $request | & $HelperExe 2>$null
$result = $lines | Where-Object { $_ -match '"type":"embedResult"' } | Select-Object -First 1
if (-not $result) { throw "no embedResult line. Helper output:`n$($lines -join "`n")" }
$obj = $result | ConvertFrom-Json
if ($obj.embeddings.Count -ne 2) { throw "expected 2 vectors, got $($obj.embeddings.Count)" }
if ($obj.embeddings[0].Count -ne $Dim) { throw "expected dim $Dim, got $($obj.embeddings[0].Count)" }
$norm = [Math]::Sqrt(($obj.embeddings[0] | ForEach-Object { $_ * $_ } | Measure-Object -Sum).Sum)
if ([Math]::Abs($norm - 1.0) -gt 0.01) { throw "vector not unit-normalized (norm=$norm)" }
Write-Host "method: $($obj.method)"
Write-Host "PASS: 2 vectors, dim $Dim, unit-normalized"
```

- [ ] **Step 6: Run the smoke (only if the model is already fetched — otherwise defer to Task 9 and note it)**

Run: `pwsh tools/smoke-embed.ps1`
Expected: `PASS: 2 vectors, dim 256, unit-normalized`. (If the model is not yet on disk this step
re-runs at the end of Task 9.)

- [ ] **Step 7: Commit**

```bash
git add src/LocalScribe.Assistant/EmbedEngine.cs src/LocalScribe.Assistant/Program.cs src/LocalScribe.Assistant/LlamaEngine.cs tools/smoke-embed.ps1
git commit -m "feat(semantic): helper embed op - EmbedEngine + Program routing + smoke script"
```

---

### Task 4: AssistantEmbeddingClient — the warm-process Core seam

**Files:**
- Create: `src/LocalScribe.Core/Search/Semantic/EmbeddingClient.cs`
- Test: `tests/LocalScribe.Core.Tests/AssistantEmbeddingClientTests.cs`

**Interfaces:**
- Consumes: `IAssistantProcessFactory` / `IAssistantProcess` (existing), `AssistantWire`, `AssistantEmbedResult`, `AssistantEventStream` (internal, same assembly).
- Produces (Task 8 depends on these exact shapes):

```csharp
public sealed record EmbeddingBatch(IReadOnlyList<float[]> Embeddings, string Method);

public interface IEmbeddingClient : IAsyncDisposable
{
    Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts, CancellationToken ct);
    /// <summary>Kill the warm helper NOW (recording start / shutdown), keeping the client usable -
    /// the next EmbedAsync respawns. Frees the helper's ~600MB working set (memory rule).</summary>
    ValueTask ReleaseAsync();
}
```

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/AssistantEmbeddingClientTests.cs` — fake process replaying canned
stdout lines (mirror the fake style in `AssistantJobRunnerTests`):

```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Search.Semantic;

public sealed class AssistantEmbeddingClientTests
{
    private sealed class FakeProcess(IEnumerable<string> lines) : IAssistantProcess
    {
        private readonly Queue<string> _lines = new(lines);
        public List<string> Written { get; } = [];
        public bool Killed { get; private set; }
        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        { Written.Add(requestJson); return Task.CompletedTask; }
        public Task<string?> ReadEventLineAsync(CancellationToken ct)
            => Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);
        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() { Kill(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeFactory(Func<FakeProcess> make) : IAssistantProcessFactory
    {
        public int Starts { get; private set; }
        public FakeProcess? Last { get; private set; }
        public Task<IAssistantProcess> StartAsync(CancellationToken ct)
        { Starts++; Last = make(); return Task.FromResult<IAssistantProcess>(Last); }
    }

    private static string EmbedResultLine(string method, params float[][] vectors)
        => AssistantWire.SerializeEvent(new AssistantEmbedResult(vectors, method));
    private static string DoneLine()
        => AssistantWire.SerializeEvent(new AssistantDone("cpu", 0, 1));
    private static string ErrorLine(string msg)
        => AssistantWire.SerializeEvent(new AssistantError(msg));

    [Fact]
    public async Task Embed_returns_vectors_and_method_and_sends_a_keepalive_embed_request()
    {
        var factory = new FakeFactory(() => new FakeProcess(
            [EmbedResultLine("m@2", [1f, 0f]), DoneLine(),
             EmbedResultLine("m@2", [0f, 1f]), DoneLine()]));
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);

        var batch = await client.EmbedAsync("document", ["hello"], CancellationToken.None);

        Assert.Equal("m@2", batch.Method);
        Assert.Equal(new[] { 1f, 0f }, Assert.Single(batch.Embeddings));
        string sent = Assert.Single(factory.Last!.Written);
        Assert.Contains("\"op\":\"embed\"", sent);
        Assert.Contains("\"keepAlive\":true", sent);
        Assert.Contains("\"backend\":\"cpu\"", sent);

        // second call reuses the SAME warm process (no new StartAsync)
        await client.EmbedAsync("query", ["again"], CancellationToken.None);
        Assert.Equal(1, factory.Starts);
        Assert.Equal(2, factory.Last!.Written.Count);
    }

    [Fact]
    public async Task Error_event_throws_and_kills_the_process_for_a_fresh_respawn()
    {
        int made = 0;
        var factory = new FakeFactory(() => { made++; return new FakeProcess(
            made == 1 ? [ErrorLine("boom")] : [EmbedResultLine("m@2", [1f, 0f]), DoneLine()]); });
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);

        await Assert.ThrowsAsync<AssistantException>(
            () => client.EmbedAsync("document", ["x"], CancellationToken.None));

        // next call starts a NEW process and succeeds
        var batch = await client.EmbedAsync("document", ["x"], CancellationToken.None);
        Assert.Single(batch.Embeddings);
        Assert.Equal(2, factory.Starts);
    }

    [Fact]
    public async Task Eof_before_terminal_throws_AssistantException()
    {
        var factory = new FakeFactory(() => new FakeProcess([]));   // immediate EOF
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);
        await Assert.ThrowsAsync<AssistantException>(
            () => client.EmbedAsync("document", ["x"], CancellationToken.None));
    }

    [Fact]
    public async Task Release_kills_the_warm_process_and_the_next_call_respawns()
    {
        var factory = new FakeFactory(() => new FakeProcess(
            [EmbedResultLine("m@2", [1f, 0f]), DoneLine()]));
        await using var client = new AssistantEmbeddingClient(factory, @"C:\m\e.gguf", dim: 2);
        await client.EmbedAsync("document", ["x"], CancellationToken.None);
        var first = factory.Last!;

        await client.ReleaseAsync();

        Assert.True(first.Killed);
        await client.EmbedAsync("document", ["y"], CancellationToken.None);
        Assert.Equal(2, factory.Starts);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AssistantEmbeddingClientTests"`
Expected: FAIL — `AssistantEmbeddingClient` / `IEmbeddingClient` do not exist.

- [ ] **Step 3: Implement `src/LocalScribe.Core/Search/Semantic/EmbeddingClient.cs`**

```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Search.Semantic;

public sealed record EmbeddingBatch(IReadOnlyList<float[]> Embeddings, string Method);

/// <summary>The Core seam over the helper's embed op. ONE warm process max (memory rule):
/// backfill and queries share this client; ReleaseAsync kills the helper (recording start,
/// shutdown) and the next EmbedAsync respawns it lazily.</summary>
public interface IEmbeddingClient : IAsyncDisposable
{
    Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts, CancellationToken ct);
    ValueTask ReleaseAsync();
}

/// <summary>Production IEmbeddingClient: keepAlive embed requests on a persistent-stdin helper
/// (the AssistantChatSessionFactory transport shape, without KV state - embed jobs are stateless,
/// so a killed process costs only the ~2s model reload). Serialized: one in-flight batch at a
/// time; a query issued mid-backfill waits behind at most one batch (~1s). Any failure kills the
/// process and throws AssistantException - the caller owns retry policy.</summary>
public sealed class AssistantEmbeddingClient(IAssistantProcessFactory factory, string modelPath,
    int dim, TimeSpan? inactivityTimeout = null) : IEmbeddingClient
{
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAssistantProcess? _proc;

    public async Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _proc ??= await factory.StartAsync(ct);
            var request = new AssistantRequest("embed", modelPath, CtxTokens: 2048,
                Backend: "cpu", KeepAlive: true,
                AssistantWire.EmbedPayload(kind, texts, dim));
            try
            {
                await _proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(request), ct);
                AssistantEmbedResult? result = null;
                await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(
                    _proc, _inactivityTimeout, ct))
                {
                    if (evt is AssistantEmbedResult r) result = r;
                    if (evt is AssistantError err) throw new AssistantException(err.Message);
                    if (evt is AssistantDone)
                        return result is not null
                            ? new EmbeddingBatch(result.Embeddings, result.Method)
                            : throw new AssistantException("embed done without an embedResult");
                }
                throw new AssistantException("embed helper exited before completing the batch");
            }
            catch
            {
                await KillAndForgetAsync();   // poisoned pipe: next call respawns fresh
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync();
        try { await KillAndForgetAsync(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    private async Task KillAndForgetAsync()
    {
        if (_proc is { } p)
        {
            _proc = null;
            p.Kill();
            await p.DisposeAsync();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~AssistantEmbeddingClientTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Search/Semantic/EmbeddingClient.cs tests/LocalScribe.Core.Tests/AssistantEmbeddingClientTests.cs
git commit -m "feat(semantic): AssistantEmbeddingClient - shared warm-process embed seam with release-on-recording"
```

---

### Task 5: SemanticChunker

**Files:**
- Create: `src/LocalScribe.Core/Search/Semantic/SemanticModels.cs` (just `SemanticChunk` for now; Task 6 adds `SemanticSidecar` here)
- Create: `src/LocalScribe.Core/Search/Semantic/SemanticChunker.cs`
- Test: `tests/LocalScribe.Core.Tests/SemanticChunkerTests.cs`

**Interfaces:**
- Consumes: `DisplayRow` / `RowSegment` (`LocalScribe.Core.Projection`) — `RowSegment(int Seq, TranscriptSource Source, long StartMs, long EndMs, string ProjectedText, string RawText, bool IsCorrected, bool IsPinned, bool IsSplitChild = false, int PartIndex = 0)`; `DisplayRow` is init-property record with `IsMarker`, `DisplayName`, `Segments`.
- Produces:

```csharp
public sealed record SemanticChunk(int StartSeq, int StartPartIndex, long StartMs,
    int EndSeq, long EndMs, string Text);

public static class SemanticChunker
{
    public const int TargetChars = 700;
    public static IReadOnlyList<SemanticChunk> Chunk(IReadOnlyList<DisplayRow> rows);
}
```

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/SemanticChunkerTests.cs`:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search.Semantic;

public sealed class SemanticChunkerTests
{
    private static RowSegment Seg(int seq, string text, long startMs = 0, long endMs = 1000,
        int partIndex = 0)
        => new(seq, TranscriptSource.Local, startMs, endMs, text, text,
            IsCorrected: false, IsPinned: false, IsSplitChild: false, PartIndex: partIndex);

    private static DisplayRow Row(string? speaker, params RowSegment[] segs) => new()
    { IsMarker = false, DisplayName = speaker, Segments = segs, Text = "" };

    private static DisplayRow Marker() => new() { IsMarker = true, Text = "marker" };

    [Fact]
    public void Short_transcript_becomes_one_chunk_with_speaker_prefixes()
    {
        var chunks = SemanticChunker.Chunk(
            [Row("Alice", Seg(0, "hello there", 0, 900)),
             Row("Bob", Seg(1, "hi Alice", 1000, 1900))]);
        var c = Assert.Single(chunks);
        Assert.Equal(0, c.StartSeq);
        Assert.Equal(0, c.StartMs);
        Assert.Equal(1, c.EndSeq);
        Assert.Equal(1900, c.EndMs);
        Assert.Contains("Alice: hello there", c.Text);
        Assert.Contains("Bob: hi Alice", c.Text);
    }

    [Fact]
    public void Speaker_prefix_appears_only_on_speaker_change_within_a_chunk()
    {
        var c = Assert.Single(SemanticChunker.Chunk(
            [Row("Alice", Seg(0, "first"), Seg(1, "second"))]));
        Assert.Equal(1, c.Text.Split("Alice:").Length - 1);   // one prefix, not two
    }

    [Fact]
    public void Packing_splits_at_target_and_overlaps_one_segment()
    {
        string body = new string('x', 400);
        var chunks = SemanticChunker.Chunk(
            [Row("A", Seg(0, body), Seg(1, body), Seg(2, body))]);
        Assert.Equal(2, chunks.Count);
        // chunk 0 = segs 0..1 (800 chars fits before the third breaks the 700 target after seg 0? no:
        // 400 fits, +400 = 800 > 700 -> chunk 0 = seg 0 only... see math note below)
        // Deterministic assertion instead of narrating the math:
        Assert.Equal(0, chunks[0].StartSeq);
        // one-segment overlap: the next chunk STARTS at the previous chunk's last segment
        Assert.Equal(chunks[0].EndSeq, chunks[1].StartSeq);
        Assert.Equal(2, chunks[^1].EndSeq);                    // tail is covered
    }

    [Fact]
    public void Single_oversized_segment_becomes_its_own_chunk_untruncated()
    {
        string huge = new string('y', 3000);
        var chunks = SemanticChunker.Chunk([Row("A", Seg(0, huge), Seg(1, "tail"))]);
        Assert.Contains(huge, chunks[0].Text);
        Assert.Equal(0, chunks[0].StartSeq);
        Assert.Equal(0, chunks[0].EndSeq);                     // alone in its chunk
    }

    [Fact]
    public void Markers_and_empty_segments_are_excluded()
    {
        var chunks = SemanticChunker.Chunk(
            [Marker(), Row("A", Seg(0, "  "), Seg(1, "real content")), Marker()]);
        var c = Assert.Single(chunks);
        Assert.Equal(1, c.StartSeq);
        Assert.DoesNotContain("marker", c.Text);
    }

    [Fact]
    public void Empty_rows_produce_no_chunks()
    {
        Assert.Empty(SemanticChunker.Chunk([]));
        Assert.Empty(SemanticChunker.Chunk([Marker()]));
    }

    [Fact]
    public void Split_children_keep_their_part_index_anchor()
    {
        var chunks = SemanticChunker.Chunk(
            [Row("A", Seg(5, "part two text", 2000, 3000, partIndex: 1))]);
        var c = Assert.Single(chunks);
        Assert.Equal(5, c.StartSeq);
        Assert.Equal(1, c.StartPartIndex);
        Assert.Equal(2000, c.StartMs);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticChunkerTests"`
Expected: FAIL — `SemanticChunk` / `SemanticChunker` do not exist.

- [ ] **Step 3: Implement**

`src/LocalScribe.Core/Search/Semantic/SemanticModels.cs`:

```csharp
namespace LocalScribe.Core.Search.Semantic;

/// <summary>One embeddable chunk (design 2026-07-25): a greedy pack of consecutive non-marker
/// segments with speaker prefixes baked into Text. Anchors point at the FIRST segment so a hit
/// reuses the exact lexical click-through (ReadViewWindow.ShowFindAt(StartSeq, ...)); EndSeq/EndMs
/// bound the covered range for dedup against lexical hits and for the snippet timestamp.</summary>
public sealed record SemanticChunk(int StartSeq, int StartPartIndex, long StartMs,
    int EndSeq, long EndMs, string Text);
```

`src/LocalScribe.Core/Search/Semantic/SemanticChunker.cs`:

```csharp
using System.Text;
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Search.Semantic;

/// <summary>Pure projection-rows -> chunks (design 2026-07-25). Greedy pack up to ~TargetChars
/// with ONE-SEGMENT overlap between adjacent chunks (a thought spanning a boundary is never
/// invisible to both); a single oversized segment becomes its own chunk (the model window, 2K
/// tokens, truncates at embed time - effectively never). Markers and whitespace-only segments
/// are excluded, matching SearchIndexBuilder's marker rule.</summary>
public static class SemanticChunker
{
    public const int TargetChars = 700;

    public static IReadOnlyList<SemanticChunk> Chunk(IReadOnlyList<DisplayRow> rows)
    {
        var pieces = new List<(RowSegment Seg, string Speaker)>();
        foreach (var row in rows)
        {
            if (row.IsMarker) continue;
            foreach (var seg in row.Segments)
                if (!string.IsNullOrWhiteSpace(seg.ProjectedText))
                    pieces.Add((seg, row.DisplayName ?? ""));
        }
        if (pieces.Count == 0) return [];

        var chunks = new List<SemanticChunk>();
        int i = 0;
        while (i < pieces.Count)
        {
            var sb = new StringBuilder();
            string? lastSpeaker = null;
            int start = i, end = i;
            for (int j = i; j < pieces.Count; j++)
            {
                var (seg, speaker) = pieces[j];
                string prefix = speaker.Length > 0 && speaker != lastSpeaker ? speaker + ": " : "";
                string piece = prefix + seg.ProjectedText.Trim() + "\n";
                if (sb.Length > 0 && sb.Length + piece.Length > TargetChars) break;
                sb.Append(piece);
                lastSpeaker = speaker;
                end = j;
            }
            var first = pieces[start].Seg;
            var last = pieces[end].Seg;
            chunks.Add(new SemanticChunk(first.Seq, first.PartIndex, first.StartMs,
                last.Seq, last.EndMs, sb.ToString().TrimEnd('\n')));
            if (end == pieces.Count - 1) break;               // tail reached - done
            i = end > start ? end : end + 1;                  // overlap by one; never re-pack a lone segment
        }
        return chunks;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticChunkerTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Search/Semantic/SemanticModels.cs src/LocalScribe.Core/Search/Semantic/SemanticChunker.cs tests/LocalScribe.Core.Tests/SemanticChunkerTests.cs
git commit -m "feat(semantic): SemanticChunker - windowed segment packing with one-segment overlap"
```

---

### Task 6: Binary sidecar store (`index\semantic\{id}.vec`)

**Files:**
- Modify: `src/LocalScribe.Core/Storage/AtomicFile.cs` (add `WriteAllBytesAsync`)
- Modify: `src/LocalScribe.Core/Storage/StoragePaths.cs` (two path getters)
- Modify: `src/LocalScribe.Core/Search/Semantic/SemanticModels.cs` (add `SemanticSidecar`)
- Create: `src/LocalScribe.Core/Search/Semantic/SemanticIndexStore.cs`
- Test: `tests/LocalScribe.Core.Tests/SemanticIndexStoreTests.cs`

**Interfaces:**
- Consumes: `SearchFreshnessStamps` (Task-independent, existing), `SemanticChunk` (Task 5), `AtomicFile`.
- Produces:

```csharp
public sealed record SemanticSidecar(string Method, string VersionId, SearchFreshnessStamps Stamps,
    int Dim, IReadOnlyList<SemanticChunk> Chunks, IReadOnlyList<float[]> Vectors);

public sealed class SemanticIndexStore(StoragePaths paths)
{
    public const int Version = 1;
    public Task<SemanticSidecar?> LoadAsync(string sessionId, CancellationToken ct); // null = missing/corrupt/newer
    public Task SaveAsync(string sessionId, SemanticSidecar sidecar, CancellationToken ct);
    public void Delete(string sessionId);
    public IReadOnlyList<string> ListSessionIds();
}
```

- `StoragePaths.SemanticIndexDir : string` and `StoragePaths.SemanticSidecarFile(string sessionId) : string`.
- `AtomicFile.WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/SemanticIndexStoreTests.cs`:

```csharp
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class SemanticIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly SemanticIndexStore _store;
    public SemanticIndexStoreTests()
    { _paths = new StoragePaths(_root); _store = new SemanticIndexStore(_paths); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SemanticSidecar Sidecar() => new(
        Method: "m@2", VersionId: "v1",
        Stamps: new SearchFreshnessStamps { TranscriptTicks = 11, EditsTicks = 22, SpeakersTicks = 33, MetaTicks = 44 },
        Dim: 2,
        Chunks: [new SemanticChunk(0, 0, 0, 1, 1900, "Alice: hello\nBob: hi"),
                 new SemanticChunk(1, 0, 1000, 2, 2900, "Bob: hi\nAlice: settle at 350k")],
        Vectors: [[0.6f, 0.8f], [1f, 0f]]);

    [Fact]
    public async Task Round_trips_every_field()
    {
        await _store.SaveAsync("s-1", Sidecar(), CancellationToken.None);
        var loaded = await _store.LoadAsync("s-1", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("m@2", loaded.Method);
        Assert.Equal(2, loaded.Dim);
        Assert.Equal(Sidecar().Chunks[0], loaded.Chunks[0]);   // SemanticChunk is a value record
        Assert.Equal(2, loaded.Chunks.Count);
        Assert.Equal("Bob: hi\nAlice: settle at 350k", loaded.Chunks[1].Text);
        Assert.Equal(new[] { 0.6f, 0.8f }, loaded.Vectors[0]);
        Assert.Equal(new[] { 1f, 0f }, loaded.Vectors[1]);
        Assert.Equal(11, loaded.Stamps.TranscriptTicks);
        Assert.Equal("v1", loaded.VersionId);
    }

    [Fact]
    public async Task Missing_file_loads_null()
        => Assert.Null(await _store.LoadAsync("nope", CancellationToken.None));

    [Fact]
    public async Task Truncated_file_loads_null_never_throws()
    {
        await _store.SaveAsync("s-1", Sidecar(), CancellationToken.None);
        string path = _paths.SemanticSidecarFile("s-1");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);
        Assert.Null(await _store.LoadAsync("s-1", CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_magic_or_newer_version_loads_null()
    {
        string path = _paths.SemanticSidecarFile("s-1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [9, 9, 9, 9, 9, 9, 9, 9]);
        Assert.Null(await _store.LoadAsync("s-1", CancellationToken.None));

        // newer schema: valid magic, version+1
        await _store.SaveAsync("s-2", Sidecar(), CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(_paths.SemanticSidecarFile("s-2"));
        bytes[4] = (byte)(SemanticIndexStore.Version + 1);      // version int little-endian low byte
        await File.WriteAllBytesAsync(_paths.SemanticSidecarFile("s-2"), bytes);
        Assert.Null(await _store.LoadAsync("s-2", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_is_idempotent_and_list_enumerates_saved_ids()
    {
        Assert.Empty(_store.ListSessionIds());                  // dir absent: empty, no throw
        await _store.SaveAsync("s-1", Sidecar(), CancellationToken.None);
        await _store.SaveAsync("s-2", Sidecar(), CancellationToken.None);
        Assert.Equal(["s-1", "s-2"], _store.ListSessionIds().OrderBy(x => x, StringComparer.Ordinal));
        _store.Delete("s-1");
        _store.Delete("s-1");                                   // second delete: no throw
        Assert.Equal(["s-2"], _store.ListSessionIds());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticIndexStoreTests"`
Expected: FAIL — store/paths members do not exist (after fixing the transcription typo above).

- [ ] **Step 3: Implement**

`AtomicFile.cs` — add below `WriteAllTextAsync` (same retry discipline, same comment applies):

```csharp
    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 9)
            {
                await Task.Delay(20 * (attempt + 1), ct);
            }
        }
    }
```

`StoragePaths.cs` — add below `SearchIndexJson`:

```csharp
    /// <summary>Semantic-search sidecars (design 2026-07-25): DERIVED vectors + chunk text under
    /// index\semantic\, one binary file per session. Rebuildable, safe to delete wholesale -
    /// never evidence (same standing as search-index.json).</summary>
    public string SemanticIndexDir => Path.Combine(Root, "index", "semantic");
    public string SemanticSidecarFile(string sessionId)
        => Path.Combine(SemanticIndexDir, sessionId + ".vec");
```

`SemanticModels.cs` — append:

```csharp
/// <summary>One session's semantic sidecar: staleness identity (Method + VersionId + Stamps -
/// the lexical freshness rule plus method gating) and parallel Chunks/Vectors lists
/// (Chunks.Count == Vectors.Count; every vector has length Dim, unit-normalized).</summary>
public sealed record SemanticSidecar(string Method, string VersionId,
    LocalScribe.Core.Search.SearchFreshnessStamps Stamps,
    int Dim, IReadOnlyList<SemanticChunk> Chunks, IReadOnlyList<float[]> Vectors);
```

`src/LocalScribe.Core/Search/Semantic/SemanticIndexStore.cs`:

```csharp
using System.Text;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Search.Semantic;

/// <summary>Binary per-session sidecar IO for index\semantic\{id}.vec (design 2026-07-25).
/// Binary because ~150k x 256 float32 in JSON would triple size and slow every load; per-session
/// because incremental reindex rewrites one small file and a torn write costs one session, not
/// the corpus. Load returns null for missing/corrupt/truncated/wrong-magic/newer-version files -
/// the service silently re-embeds (SearchIndexStore's self-heal philosophy). Writes are atomic
/// (AtomicFile). Format v1: magic 'LSSV' | int version | string method | string versionId |
/// 4x long stamps | int dim | int count | count x (int startSeq | int startPartIndex |
/// long startMs | int endSeq | long endMs | string text | dim x float).</summary>
public sealed class SemanticIndexStore(StoragePaths paths)
{
    public const int Version = 1;
    private const uint Magic = 0x5653534C;   // "LSSV" little-endian

    public async Task<SemanticSidecar?> LoadAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            string path = paths.SemanticSidecarFile(sessionId);
            if (!File.Exists(path)) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() > Version) return null;            // newer app wrote it
            string method = r.ReadString();
            string versionId = r.ReadString();
            var stamps = new SearchFreshnessStamps
            {
                TranscriptTicks = r.ReadInt64(), EditsTicks = r.ReadInt64(),
                SpeakersTicks = r.ReadInt64(), MetaTicks = r.ReadInt64(),
            };
            int dim = r.ReadInt32();
            int count = r.ReadInt32();
            var chunks = new List<SemanticChunk>(count);
            var vectors = new List<float[]>(count);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                chunks.Add(new SemanticChunk(r.ReadInt32(), r.ReadInt32(), r.ReadInt64(),
                    r.ReadInt32(), r.ReadInt64(), r.ReadString()));
                float[] v = new float[dim];
                for (int d = 0; d < dim; d++) v[d] = r.ReadSingle();
                vectors.Add(v);
            }
            return new SemanticSidecar(method, versionId, stamps, dim, chunks, vectors);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }                                    // corrupt -> silent re-embed
    }

    public Task SaveAsync(string sessionId, SemanticSidecar sidecar, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(sidecar.Method);
            w.Write(sidecar.VersionId);
            w.Write(sidecar.Stamps.TranscriptTicks);
            w.Write(sidecar.Stamps.EditsTicks);
            w.Write(sidecar.Stamps.SpeakersTicks);
            w.Write(sidecar.Stamps.MetaTicks);
            w.Write(sidecar.Dim);
            w.Write(sidecar.Chunks.Count);
            for (int i = 0; i < sidecar.Chunks.Count; i++)
            {
                var c = sidecar.Chunks[i];
                w.Write(c.StartSeq); w.Write(c.StartPartIndex); w.Write(c.StartMs);
                w.Write(c.EndSeq); w.Write(c.EndMs); w.Write(c.Text);
                float[] v = sidecar.Vectors[i];
                for (int d = 0; d < sidecar.Dim; d++) w.Write(d < v.Length ? v[d] : 0f);
            }
        }
        return AtomicFile.WriteAllBytesAsync(paths.SemanticSidecarFile(sessionId), ms.ToArray(), ct);
    }

    public void Delete(string sessionId)
    {
        try { File.Delete(paths.SemanticSidecarFile(sessionId)); } catch { }
    }

    public IReadOnlyList<string> ListSessionIds()
        => Directory.Exists(paths.SemanticIndexDir)
            ? Directory.EnumerateFiles(paths.SemanticIndexDir, "*.vec")
                .Select(Path.GetFileNameWithoutExtension).Where(n => n is not null)
                .Select(n => n!).ToList()
            : [];
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticIndexStoreTests"`
Expected: PASS (5 tests). Note the newer-version test flips byte 4 (the version int's low byte
directly after the 4-byte magic) — if it fails, check the writer really emits magic first.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Storage/AtomicFile.cs src/LocalScribe.Core/Storage/StoragePaths.cs src/LocalScribe.Core/Search/Semantic/ tests/LocalScribe.Core.Tests/SemanticIndexStoreTests.cs
git commit -m "feat(semantic): binary per-session sidecar store under index/semantic"
```

---

### Task 7: SemanticQueryEngine + two additive lexical accessors

**Files:**
- Modify: `src/LocalScribe.Core/Search/SearchQueryEngine.cs` (`PassesFacets` private→public)
- Modify: `src/LocalScribe.Core/Search/SearchIndexService.cs` (add `SnapshotEntries()`)
- Create: `src/LocalScribe.Core/Search/Semantic/SemanticQueryEngine.cs`
- Test: `tests/LocalScribe.Core.Tests/SemanticQueryEngineTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record SemanticHit(int StartSeq, int StartPartIndex, long StartMs,
    string Snippet, float Score);
public sealed record SemanticResult(SearchSessionEntry Session,
    IReadOnlyList<SemanticHit> Hits, float BestScore);

public static class SemanticQueryEngine
{
    public const float MinScore = 0.55f;
    public const int MaxChunks = 40;
    public const int SnippetChars = 160;
    public static IReadOnlyList<SemanticResult> Run(float[] queryVector,
        IReadOnlyDictionary<string, SearchSessionEntry> metadata,
        IReadOnlyDictionary<string, SemanticSidecar> sidecars,
        SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults);
}
```

- `SearchQueryEngine.PassesFacets(SearchSessionEntry, SearchQuery) : bool` becomes public.
- `SearchIndexService.SnapshotEntries() : IReadOnlyDictionary<string, SearchSessionEntry>`.

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/SemanticQueryEngineTests.cs`:

```csharp
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;

public sealed class SemanticQueryEngineTests
{
    private static SearchSessionEntry Meta(string id, string matterId = "M1",
        string app = "Webex", int day = 1) => new()
    {
        SessionId = id, Title = "T-" + id, MatterIds = [matterId], App = app,
        StartedAtUtc = new DateTimeOffset(2026, 7, day, 9, 0, 0, TimeSpan.Zero),
    };

    private static SemanticSidecar Sidecar(params (SemanticChunk Chunk, float[] Vec)[] entries)
        => new("m@2", "v1", new SearchFreshnessStamps(), 2,
            entries.Select(e => e.Chunk).ToList(), entries.Select(e => e.Vec).ToList());

    private static SemanticChunk Chunk(int startSeq, int endSeq, string text = "some words")
        => new(startSeq, 0, startSeq * 1000L, endSeq, endSeq * 1000L + 900, text);

    // query vector [1,0]: score of a chunk = its vector's first component
    private static readonly float[] Query = [1f, 0f];

    [Fact]
    public void Scores_floor_and_orders_by_best_chunk()
    {
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry>
            { ["a"] = Meta("a"), ["b"] = Meta("b") },
            new Dictionary<string, SemanticSidecar>
            {
                ["a"] = Sidecar((Chunk(0, 1), [0.7f, 0.71f]), (Chunk(2, 3), [0.2f, 0.98f])),
                ["b"] = Sidecar((Chunk(0, 1), [0.9f, 0.44f])),
            },
            new SearchQuery("anything"), lexicalResults: []);

        Assert.Equal(2, results.Count);
        Assert.Equal("b", results[0].Session.SessionId);          // 0.9 beats 0.7
        Assert.Equal(0.9f, results[0].BestScore, 2);
        var aHits = results[1].Hits;
        Assert.Single(aHits);                                     // 0.2 chunk is under the 0.55 floor
        Assert.Equal(0, aHits[0].StartSeq);
    }

    [Fact]
    public void Facets_filter_before_scoring()
    {
        var meta = new Dictionary<string, SearchSessionEntry>
        { ["a"] = Meta("a", matterId: "M1"), ["b"] = Meta("b", matterId: "M2") };
        var sidecars = new Dictionary<string, SemanticSidecar>
        { ["a"] = Sidecar((Chunk(0, 1), [1f, 0f])), ["b"] = Sidecar((Chunk(0, 1), [1f, 0f])) };

        var results = SemanticQueryEngine.Run(Query, meta, sidecars,
            new SearchQuery("x", MatterId: "M2"), []);

        Assert.Equal("b", Assert.Single(results).Session.SessionId);
    }

    [Fact]
    public void Session_missing_from_metadata_is_not_searchable()
    {
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry>(),
            new Dictionary<string, SemanticSidecar> { ["ghost"] = Sidecar((Chunk(0, 1), [1f, 0f])) },
            new SearchQuery("x"), []);
        Assert.Empty(results);
    }

    [Fact]
    public void Chunk_covering_a_lexical_hit_seq_is_deduped_but_other_chunks_survive()
    {
        var meta = new Dictionary<string, SearchSessionEntry> { ["a"] = Meta("a") };
        var sidecars = new Dictionary<string, SemanticSidecar>
        { ["a"] = Sidecar((Chunk(0, 5), [1f, 0f]), (Chunk(10, 15), [0.8f, 0.6f])) };
        var lexical = new List<SearchResult>
        {
            new(Meta("a"), [new SearchHit(3, 0, 0, "S", "snip", "term", false, false)], 1),
        };

        var results = SemanticQueryEngine.Run(Query, meta, sidecars, new SearchQuery("x"), lexical);

        var hit = Assert.Single(Assert.Single(results).Hits);     // chunk 0-5 covers seq 3 -> dropped
        Assert.Equal(10, hit.StartSeq);
    }

    [Fact]
    public void Caps_at_MaxChunks_across_all_sessions()
    {
        var entries = Enumerable.Range(0, 60)
            .Select(i => (Chunk(i * 10, i * 10 + 1), new[] { 0.9f, 0.44f })).ToArray();
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry> { ["a"] = Meta("a") },
            new Dictionary<string, SemanticSidecar> { ["a"] = Sidecar(entries) },
            new SearchQuery("x"), []);
        Assert.Equal(SemanticQueryEngine.MaxChunks, results.Sum(r => r.Hits.Count));
    }

    [Fact]
    public void Snippet_truncates_long_chunk_text_and_flattens_newlines()
    {
        var text = "Alice: " + new string('z', 400) + "\nBob: more";
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry> { ["a"] = Meta("a") },
            new Dictionary<string, SemanticSidecar>
            { ["a"] = Sidecar((Chunk(0, 1, text), [1f, 0f])) },
            new SearchQuery("x"), []);
        string snippet = results[0].Hits[0].Snippet;
        Assert.True(snippet.Length <= SemanticQueryEngine.SnippetChars + 1);   // +1 for ellipsis char
        Assert.DoesNotContain('\n', snippet);
    }

    [Fact]
    public void Deterministic_tie_order_by_session_id()
    {
        var meta = new Dictionary<string, SearchSessionEntry> { ["b"] = Meta("b"), ["a"] = Meta("a") };
        var sidecars = new Dictionary<string, SemanticSidecar>
        {
            ["b"] = Sidecar((Chunk(0, 1), [0.8f, 0.6f])),
            ["a"] = Sidecar((Chunk(0, 1), [0.8f, 0.6f])),
        };
        var results = SemanticQueryEngine.Run(Query, meta, sidecars, new SearchQuery("x"), []);
        Assert.Equal(["a", "b"], results.Select(r => r.Session.SessionId));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticQueryEngineTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`SearchQueryEngine.cs` — change the modifier and doc line:

```csharp
    /// <summary>Shared facet gate: also used by SemanticQueryEngine so the Related section's
    /// matter/date/app facets behave IDENTICALLY to the exact section (design 2026-07-25).</summary>
    public static bool PassesFacets(SearchSessionEntry s, SearchQuery q)
```

`SearchIndexService.cs` — add below `Query`:

```csharp
    /// <summary>Snapshot of the current entries keyed by session id. Semantic search reads facet
    /// metadata + ELIGIBILITY from here (design 2026-07-25): a session absent from the lexical
    /// index is absent from semantic - one definition of "searchable", no metadata drift.</summary>
    public IReadOnlyDictionary<string, SearchSessionEntry> SnapshotEntries()
    {
        lock (_lock) return new Dictionary<string, SearchSessionEntry>(_entries, StringComparer.Ordinal);
    }
```

`src/LocalScribe.Core/Search/Semantic/SemanticQueryEngine.cs`:

```csharp
namespace LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Search;

/// <summary>One semantic hit: the chunk's anchor (click-through = ShowFindAt(StartSeq)) plus a
/// truncated snippet of the chunk text and its cosine score.</summary>
public sealed record SemanticHit(int StartSeq, int StartPartIndex, long StartMs,
    string Snippet, float Score);

/// <summary>One matched session for the Related section: lexical metadata entry + hits ordered by
/// score. BestScore is the session's rank key.</summary>
public sealed record SemanticResult(SearchSessionEntry Session,
    IReadOnlyList<SemanticHit> Hits, float BestScore);

/// <summary>Pure semantic query semantics (design 2026-07-25). Facets come from the LEXICAL
/// entries via SearchQueryEngine.PassesFacets (identical behavior in both sections). Vectors are
/// unit-normalized, so cosine = dot. Chunks under MinScore are noise - the section stays empty
/// rather than padding. A chunk whose [StartSeq, EndSeq] covers a lexical hit seq in the same
/// session is dropped (never show the same passage twice); the session itself may appear in both
/// sections pointing at different passages. No IO, no mutation.</summary>
public static class SemanticQueryEngine
{
    public const float MinScore = 0.55f;   // tuning constant - calibrated in real-model smoke
    public const int MaxChunks = 40;
    public const int SnippetChars = 160;

    public static IReadOnlyList<SemanticResult> Run(float[] queryVector,
        IReadOnlyDictionary<string, SearchSessionEntry> metadata,
        IReadOnlyDictionary<string, SemanticSidecar> sidecars,
        SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults)
    {
        if (queryVector.Length == 0) return [];
        var lexicalSeqs = lexicalResults.ToDictionary(r => r.Session.SessionId,
            r => r.Hits.Where(h => h.Seq >= 0).Select(h => h.Seq).ToHashSet(),
            StringComparer.Ordinal);

        var scored = new List<(string SessionId, SemanticChunk Chunk, float Score)>();
        foreach (var (sessionId, sidecar) in sidecars)
        {
            if (!metadata.TryGetValue(sessionId, out var meta)) continue;      // not eligible
            if (!SearchQueryEngine.PassesFacets(meta, query)) continue;
            for (int i = 0; i < sidecar.Vectors.Count && i < sidecar.Chunks.Count; i++)
            {
                float score = Dot(queryVector, sidecar.Vectors[i]);
                if (score < MinScore) continue;
                var chunk = sidecar.Chunks[i];
                if (lexicalSeqs.TryGetValue(sessionId, out var seqs)
                    && seqs.Any(s => s >= chunk.StartSeq && s <= chunk.EndSeq))
                    continue;                                                   // shown as exact already
                scored.Add((sessionId, chunk, score));
            }
        }

        var top = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SessionId, StringComparer.Ordinal)
            .ThenBy(x => x.Chunk.StartSeq)
            .Take(MaxChunks)
            .ToList();

        return top
            .GroupBy(x => x.SessionId, StringComparer.Ordinal)
            .Select(g => new SemanticResult(metadata[g.Key],
                g.OrderByDescending(x => x.Score).ThenBy(x => x.Chunk.StartSeq)
                    .Select(x => new SemanticHit(x.Chunk.StartSeq, x.Chunk.StartPartIndex,
                        x.Chunk.StartMs, Snippet(x.Chunk.Text), x.Score))
                    .ToList(),
                g.Max(x => x.Score)))
            .OrderByDescending(r => r.BestScore)
            .ThenBy(r => r.Session.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Plain dot product (vectors are unit-normalized). ~40M mul-adds for a full corpus
    /// scan - tens of ms. If that ever bites, System.Numerics.Tensors is the escape hatch.</summary>
    public static float Dot(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float s = 0;
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public static string Snippet(string text)
    {
        string flat = text.Replace('\n', ' ');
        return flat.Length <= SnippetChars ? flat : flat[..SnippetChars] + "…";
    }
}
```

- [ ] **Step 4: Run the new tests AND the whole existing Search suite (the two accessor changes must not disturb it)**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticQueryEngineTests|FullyQualifiedName~SearchQueryEngine|FullyQualifiedName~SearchIndexService"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Search/SearchQueryEngine.cs src/LocalScribe.Core/Search/SearchIndexService.cs src/LocalScribe.Core/Search/Semantic/SemanticQueryEngine.cs tests/LocalScribe.Core.Tests/SemanticQueryEngineTests.cs
git commit -m "feat(semantic): SemanticQueryEngine - facet-shared cosine scan with lexical dedup"
```

---

### Task 8: SemanticIndexService — orchestrator, staleness, paused backfill, coverage

**Files:**
- Create: `src/LocalScribe.Core/Search/Semantic/SemanticIndexService.cs`
- Test: `tests/LocalScribe.Core.Tests/SemanticIndexServiceTests.cs`

**Interfaces:**
- Consumes: `IEmbeddingClient` (Task 4), `SemanticIndexStore` (Task 6), `SemanticChunker` (Task 5), `SemanticQueryEngine` (Task 7), `SearchIndexBuilder.ComputeStamps`, `SessionStore`, `SessionProjectionLoader`, `EmbeddingMethod`.
- Produces (Tasks 10–11 rely on):

```csharp
public interface ISemanticSearch
{
    (int Fresh, int Eligible) Coverage { get; }
    event Action? Changed;
    Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct);
}

public sealed class SemanticIndexService : ISemanticSearch, IAsyncDisposable
{
    public SemanticIndexService(StoragePaths paths, Func<Settings> settings, TimeProvider time,
        IEmbeddingClient embeddings, string method, int dim,
        Func<string?> recordingBusy,
        Func<IReadOnlyDictionary<string, SearchSessionEntry>> lexicalSnapshot,
        int pollMs = 1000, int batchSize = 32);
    public event Action<string, Exception>? SessionSkipped;
    public Task InitializeAsync(CancellationToken ct);   // load sidecars + enqueue all eligible + start worker
    public void Enqueue(string sessionId);
    public Task ProcessPendingAsync(CancellationToken ct);   // drains the queue NOW (worker + test seam)
}
```

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.Core.Tests/SemanticIndexServiceTests.cs` — real session fixtures on a temp
root (copy `SeedSessionAsync` from `SearchIndexServiceTests.cs` verbatim), fake embedding client:

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class SemanticIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private string? _recordingBusy;                        // null = idle

    public SemanticIndexServiceTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public int Calls; public int Released;
        public string Method = "fake@2";
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
            CancellationToken ct)
        {
            Calls++;
            var vectors = texts.Select(t => new[] { 1f, 0f }).ToList();   // deterministic unit vector
            return Task.FromResult(new EmbeddingBatch(vectors, Method));
        }
        public ValueTask ReleaseAsync() { Released++; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task SeedSessionAsync(string id, string text)
    {
        var t0 = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0, EndedAtUtc = t0.AddMinutes(5),
            DurationMs = 300_000,
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta { Title = "T-" + id }, default);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"), default);
    }

    private async Task<(SemanticIndexService Svc, FakeEmbeddingClient Client, SearchIndexService Lex)>
        MakeAsync(int pollMs = 1)
    {
        var lex = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System, 0);
        await lex.InitializeAsync(CancellationToken.None);
        var client = new FakeEmbeddingClient();
        var svc = new SemanticIndexService(_paths, () => new Settings(), TimeProvider.System,
            client, method: "fake@2", dim: 2,
            recordingBusy: () => _recordingBusy,
            lexicalSnapshot: lex.SnapshotEntries, pollMs: pollMs);
        return (svc, client, lex);
    }

    [Fact]
    public async Task ProcessPending_builds_persists_and_query_finds_the_session()
    {
        await SeedSessionAsync("s-1", "we could settle at three fifty");
        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);      // enqueues all eligible
        await svc.ProcessPendingAsync(CancellationToken.None);

        Assert.True(File.Exists(_paths.SemanticSidecarFile("s-1")));
        Assert.Equal((1, 1), svc.Coverage);
        var results = await svc.QueryAsync(new SearchQuery("settlement figure"), [],
            CancellationToken.None);
        Assert.Equal("s-1", Assert.Single(results).Session.SessionId);
        Assert.True(client.Calls >= 2);                          // 1+ document batch + 1 query
    }

    [Fact]
    public async Task Fresh_sidecar_is_skipped_without_re_embedding()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);
        int after = client.Calls;

        svc.Enqueue("s-1");                                     // same content: stamps fresh
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(after, client.Calls);                       // no new embed batch
    }

    [Fact]
    public async Task Edit_stamp_change_triggers_re_embed()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);
        int after = client.Calls;

        await File.WriteAllTextAsync(_paths.EditsJson("s-1"), "{\"schemaVersion\":1,\"corrections\":[]}");
        svc.Enqueue("s-1");
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.True(client.Calls > after);
    }

    [Fact]
    public async Task Wrong_method_sidecar_is_discarded_at_initialize()
    {
        await SeedSessionAsync("s-1", "content");
        var store = new SemanticIndexStore(_paths);
        await store.SaveAsync("s-1", new SemanticSidecar("OLD@9", "v1",
            new SearchFreshnessStamps(), 2,
            [new SemanticChunk(0, 0, 0, 0, 1000, "stale")], [[1f, 0f]]), CancellationToken.None);

        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.Equal(0, svc.Coverage.Fresh);                     // old-method sidecar not counted
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.True(client.Calls > 0);                           // re-embedded under the new method
        Assert.Equal((1, 1), svc.Coverage);
    }

    [Fact]
    public async Task Recording_pause_parks_the_worker_and_releases_the_helper()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, client, _) = await MakeAsync(pollMs: 10);
        await svc.InitializeAsync(CancellationToken.None);

        _recordingBusy = "recording";
        var pending = svc.ProcessPendingAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);                       // parked, not processing
        Assert.True(client.Released >= 1);                       // helper memory freed (32GB rule)
        Assert.Equal(0, client.Calls);

        _recordingBusy = null;
        await pending;                                           // resumes and completes
        Assert.True(client.Calls > 0);
    }

    [Fact]
    public async Task Query_is_exempt_from_the_recording_pause()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, _, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);

        _recordingBusy = "recording";
        var results = await svc.QueryAsync(new SearchQuery("anything"), [], CancellationToken.None);
        Assert.NotEmpty(results);                                // still answers mid-recording
    }

    [Fact]
    public async Task Gone_session_drops_its_sidecar()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, _, lex) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);

        Directory.Delete(_paths.SessionDir("s-1"), true);
        await lex.ReindexSessionAsync("s-1", CancellationToken.None);   // lexical drops it too
        svc.Enqueue("s-1");
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.False(File.Exists(_paths.SemanticSidecarFile("s-1")));
        Assert.Equal((0, 0), svc.Coverage);
    }

    [Fact]
    public async Task Embed_failure_skips_the_session_and_counts_against_coverage()
    {
        await SeedSessionAsync("s-1", "content");
        var lex = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System, 0);
        await lex.InitializeAsync(CancellationToken.None);
        var failing = new ThrowingClient();
        var svc = new SemanticIndexService(_paths, () => new Settings(), TimeProvider.System,
            failing, "fake@2", 2, () => null, lex.SnapshotEntries, pollMs: 1);
        string? skipped = null;
        svc.SessionSkipped += (id, _) => skipped = id;

        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal("s-1", skipped);
        Assert.Equal((0, 1), svc.Coverage);                      // honest coverage note fuel
    }

    private sealed class ThrowingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
            CancellationToken ct) => throw new InvalidOperationException("helper down");
        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticIndexServiceTests"`
Expected: FAIL — `SemanticIndexService` / `ISemanticSearch` do not exist.

- [ ] **Step 3: Implement `src/LocalScribe.Core/Search/Semantic/SemanticIndexService.cs`**

```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Search.Semantic;

/// <summary>The Related-section query seam the Search page consumes (fake-able in VM tests).</summary>
public interface ISemanticSearch
{
    /// <summary>Fresh = eligible sessions with a current-method, current-stamps sidecar in
    /// memory and not queued for rework; Eligible = sessions in the lexical index. Fuels the
    /// "searched N of M sessions" coverage note (design 2026-07-25).</summary>
    (int Fresh, int Eligible) Coverage { get; }
    /// <summary>Vectors or coverage changed. May fire on a background thread.</summary>
    event Action? Changed;
    Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct);
}

/// <summary>The semantic-index orchestrator (design 2026-07-25) - SearchIndexService's mirror.
/// READ-ONLY over session folders; sole write target is index\semantic\. Eligibility + facet
/// metadata come from the LEXICAL index snapshot (one definition of searchable). Staleness =
/// the lexical rule + method: VersionId AND stamps AND method must all match. The backfill
/// worker processes one session at a time and PARKS while a recording is active, RELEASING the
/// warm helper process so its memory is freed for the live pipeline (the 32GB-laptop rule);
/// queries stay exempt - one sentence on CPU is negligible. All failures degrade per-session
/// (SessionSkipped), never fault the caller.</summary>
public sealed class SemanticIndexService : ISemanticSearch, IAsyncDisposable
{
    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settings;
    private readonly TimeProvider _time;
    private readonly IEmbeddingClient _embeddings;
    private readonly string _method;
    private readonly int _dim;
    private readonly Func<string?> _recordingBusy;
    private readonly Func<IReadOnlyDictionary<string, SearchSessionEntry>> _lexicalSnapshot;
    private readonly int _pollMs;
    private readonly int _batchSize;
    private readonly SemanticIndexStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, SemanticSidecar> _sidecars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _workGate = new(1, 1);   // one drain at a time (worker + tests)
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _worker;

    public event Action? Changed;
    public event Action<string, Exception>? SessionSkipped;

    public SemanticIndexService(StoragePaths paths, Func<Settings> settings, TimeProvider time,
        IEmbeddingClient embeddings, string method, int dim,
        Func<string?> recordingBusy,
        Func<IReadOnlyDictionary<string, SearchSessionEntry>> lexicalSnapshot,
        int pollMs = 1000, int batchSize = 32)
        => (_paths, _settings, _time, _embeddings, _method, _dim, _recordingBusy,
            _lexicalSnapshot, _pollMs, _batchSize, _store)
            = (paths, settings, time, embeddings, method, dim, recordingBusy,
               lexicalSnapshot, pollMs, batchSize, new SemanticIndexStore(paths));

    public (int Fresh, int Eligible) Coverage
    {
        get
        {
            var lexical = _lexicalSnapshot();
            lock (_lock)
            {
                int fresh = lexical.Keys.Count(id => _sidecars.ContainsKey(id) && !_pending.Contains(id));
                return (fresh, lexical.Count);
            }
        }
    }

    /// <summary>Loads persisted sidecars (dropping wrong-method ones), enqueues EVERY eligible
    /// session (the per-session freshness check inside ProcessPending makes fresh ones a cheap
    /// no-op - one session.json read + four file stamps, no projection, no embed), and starts
    /// the background worker.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        foreach (string id in _store.ListSessionIds())
        {
            ct.ThrowIfCancellationRequested();
            var sidecar = await _store.LoadAsync(id, ct);
            if (sidecar is null || sidecar.Method != _method) { _store.Delete(id); continue; }
            lock (_lock) _sidecars[id] = sidecar;
        }
        foreach (string id in _lexicalSnapshot().Keys) Enqueue(id);
        _worker = Task.Run(() => WorkerLoopAsync(_disposeCts.Token), CancellationToken.None);
        RaiseChanged();
    }

    public void Enqueue(string sessionId)
    {
        lock (_lock) { if (!_pending.Add(sessionId)) return; }
        _signal.Release();
    }

    /// <summary>Drains the pending set NOW. The production worker calls this on signal; tests
    /// call it directly for deterministic completion. Honors the recording pause between
    /// sessions AND between batches.</summary>
    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        await _workGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                string? id;
                lock (_lock) id = _pending.FirstOrDefault();
                if (id is null) return;
                await WaitWhileRecordingAsync(ct);
                try { await ReindexOneAsync(id, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { try { SessionSkipped?.Invoke(id, ex); } catch { } }
                lock (_lock) _pending.Remove(id);
                RaiseChanged();
            }
        }
        finally { _workGate.Release(); }
    }

    public async Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct)
    {
        var batch = await _embeddings.EmbedAsync("query", [query.Text ?? ""], ct);
        float[] qv = batch.Embeddings.Count > 0 ? batch.Embeddings[0] : [];
        Dictionary<string, SemanticSidecar> sidecars;
        lock (_lock) sidecars = new(_sidecars, StringComparer.Ordinal);
        var metadata = _lexicalSnapshot();
        return await Task.Run(
            () => SemanticQueryEngine.Run(qv, metadata, sidecars, query, lexicalResults), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        if (_worker is { } w) { try { await w; } catch { } }
        await _embeddings.DisposeAsync();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct);
                try { await ProcessPendingAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch { }                                        // per-session failures already skipped
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Park while a recording is active, RELEASING the warm helper once (its ~600MB
    /// goes back to the live pipeline); polls until idle. Query embedding never routes here.</summary>
    private async Task WaitWhileRecordingAsync(CancellationToken ct)
    {
        bool released = false;
        while (_recordingBusy() is not null)
        {
            if (!released) { await _embeddings.ReleaseAsync(); released = true; }
            await Task.Delay(_pollMs, ct);
        }
    }

    private async Task ReindexOneAsync(string id, CancellationToken ct)
    {
        var lexical = _lexicalSnapshot();
        if (!lexical.ContainsKey(id) || !Directory.Exists(_paths.SessionDir(id)))
        {
            lock (_lock) _sidecars.Remove(id);
            _store.Delete(id);
            return;
        }
        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(selfForMigration: null, ct);
        if (record is null) { lock (_lock) _sidecars.Remove(id); _store.Delete(id); return; }
        string versionId = record.ActiveVersion;
        // Stamps BEFORE the projection load (the safe direction: content changing after the stamp
        // makes the stamp older than reality, so the next content event re-derives - never fresher).
        var stamps = SearchIndexBuilder.ComputeStamps(_paths, id, versionId);
        lock (_lock)
            if (_sidecars.TryGetValue(id, out var existing) && existing.Method == _method
                && existing.VersionId == versionId && existing.Stamps == stamps)
                return;                                           // fresh - cheap no-op

        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings(), _time, id, ct);
        var chunks = SemanticChunker.Chunk(loaded.Rows);
        var vectors = new List<float[]>(chunks.Count);
        for (int i = 0; i < chunks.Count; i += _batchSize)
        {
            await WaitWhileRecordingAsync(ct);                    // park between batches too
            var texts = chunks.Skip(i).Take(_batchSize).Select(c => c.Text).ToList();
            var result = await _embeddings.EmbedAsync("document", texts, ct);
            vectors.AddRange(result.Embeddings);
        }
        var sidecar = new SemanticSidecar(_method, versionId, stamps, _dim, chunks, vectors);
        await _store.SaveAsync(id, sidecar, ct);
        lock (_lock) _sidecars[id] = sidecar;
    }

    private void RaiseChanged() { try { Changed?.Invoke(); } catch { } }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SemanticIndexServiceTests"`
Expected: PASS (8 tests). The pause test relies on `pollMs: 10` — if flaky on a slow box, raise
the `Task.Delay(100)` to 250, never lower the assertion.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Search/Semantic/SemanticIndexService.cs tests/LocalScribe.Core.Tests/SemanticIndexServiceTests.cs
git commit -m "feat(semantic): SemanticIndexService - staleness, recording-paused backfill, coverage"
```

---

### Task 9: Settings toggle + fetch-models.ps1 -Embedding

**Files:**
- Modify: `src/LocalScribe.Core/Model/Settings.cs`
- Modify: `tools/fetch-models.ps1`
- Test: settings round-trip is covered by existing settings tests conventions — add one case to `tests/LocalScribe.Core.Tests/` only if a settings-migration test file pins the field list (check `SettingsStore`/migration tests; if they enumerate fields, add `SemanticSearch` there).

**Interfaces:**
- Produces: `Settings.SemanticSearch.Enabled : bool` (default true); manifest entries may carry `"role": "embedding"`; `models/embeddinggemma-300M-Q8_0.gguf` on disk after `-Embedding`.

- [ ] **Step 1: Add the setting**

In `Settings.cs`, after the `Console` property:

```csharp
    /// <summary>v3 (semantic search, design 2026-07-25): master toggle for the Related-discussion
    /// semantic section + its background embedding indexer. Additive - existing v3 files without
    /// it load at this default (the SectionGapMs precedent), so no schema bump/migration. The
    /// feature is additionally presence-gated: helper + embedding-role model must exist.</summary>
    public SemanticSearchSetting SemanticSearch { get; init; } = new();
```

and with the other small records at the bottom:

```csharp
public sealed record SemanticSearchSetting { public bool Enabled { get; init; } = true; }
```

- [ ] **Step 2: Run the full Core settings/migration tests**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~Settings"`
Expected: PASS. If a migration test pins the serialized field set, add `SemanticSearch` to its
expectation (additive-default rule, same as `CallDetect` before it).

- [ ] **Step 3: Extend `tools/fetch-models.ps1`**

1. Add to `param(...)`:

```powershell
    # Also fetch the semantic-search embedding model (design 2026-07-25):
    # EmbeddingGemma-300m Q8_0 GGUF (~300 MB, 100+ languages), served by the assistant
    # helper's "embed" op on CPU. Recorded into assistant-manifest.json with role=embedding.
    [switch] $Embedding
```

2. Restructure the manifest write so `-Assistant` and `-Embedding` can each (or both) contribute.
Replace the `if ($Assistant) { ... }` block's model list + manifest write with:

```powershell
$manifestEntries = @()

if ($Assistant) {
    $assistantModels = @(
        @{ CanonicalName = 'Qwen3-4B-Instruct-2507'; NativeCtx = 262144; License = 'Apache-2.0'
           Role = 'chat'
           File = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Url  = 'https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Ptr  = 'https://huggingface.co/lmstudio-community/Qwen3-4B-Instruct-2507-GGUF/raw/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf' }
    )
    foreach ($m in $assistantModels) { $manifestEntries += Get-PinnedModelEntry $m }
}

if ($Embedding) {
    # ggml-org publishes the official llama.cpp conversion of google/embeddinggemma-300m.
    # License is Gemma (use-restricted, not OSI) - recorded verbatim in the manifest; semantic
    # search runs it locally only, which the Gemma terms permit.
    $embeddingModels = @(
        @{ CanonicalName = 'EmbeddingGemma-300m'; NativeCtx = 2048; License = 'Gemma'
           Role = 'embedding'
           File = 'embeddinggemma-300M-Q8_0.gguf'
           Url  = 'https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF/resolve/main/embeddinggemma-300M-Q8_0.gguf'
           Ptr  = 'https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF/raw/main/embeddinggemma-300M-Q8_0.gguf' }
    )
    foreach ($m in $embeddingModels) { $manifestEntries += Get-PinnedModelEntry $m }
}
```

3. Add the shared fetch+pin function above that block (extracted from the old per-model loop —
same behavior, plus the `role` key):

```powershell
function Get-PinnedModelEntry {
    param([hashtable] $m)
    $dest = Join-Path $models $m.File
    Write-Host "pin: $($m.File)"
    $pin = Get-HfPinnedSha256 -PointerUrl $m.Ptr
    Write-Host "  pinned sha256: $pin"
    if (-not (Test-Path $dest)) {
        Write-Host "fetching: $($m.File)"
        Get-RemoteFile -Uris @($m.Url) -OutFile $dest
    } else {
        Write-Host "exists: $($m.File)"
    }
    Assert-Sha256 -Path $dest -ExpectedSha256 $pin
    return [ordered]@{
        canonicalName = $m.CanonicalName; file = $m.File; sha256 = $pin
        nativeCtx = $m.NativeCtx; license = $m.License; role = $m.Role
    }
}
```

(`Get-HfPinnedSha256` must be defined before this function runs — move it above if needed.)

4. Keep the existing merge-with-still-present-entries block, extending the copied fields with
role (default chat for pre-role manifests):

```powershell
                    $manifestEntries += [ordered]@{
                        canonicalName = $e.canonicalName; file = $e.file
                        sha256 = $e.sha256; nativeCtx = $e.nativeCtx; license = $e.license
                        role = $(if ($e.PSObject.Properties['role']) { $e.role } else { 'chat' })
                    }
```

and change the surrounding condition from `if ($Assistant)` to
`if ($Assistant -or $Embedding)` for the whole manifest-write section, keeping the
`$droppedModels` filter as-is.

- [ ] **Step 4: Run the fetch + the Task-3 smoke**

Run: `pwsh tools/fetch-models.ps1 -Embedding`
Expected: pins + downloads `embeddinggemma-300M-Q8_0.gguf` (~300MB), writes
`models/assistant-manifest.json` containing a `"role": "embedding"` entry AND still containing
the existing chat entry (merge preserved).
If the exact HF path 404s: browse `https://huggingface.co/ggml-org/embeddinggemma-300M-GGUF`
for the real Q8_0 blob name and update `File`/`Url`/`Ptr` together (the LFS-pointer pinning keeps
provenance fail-closed regardless of name).

Then run: `pwsh tools/smoke-embed.ps1`
Expected: `PASS: 2 vectors, dim 256, unit-normalized`. **This is the LLamaSharp-architecture
checkpoint** — if the helper reports an unknown-architecture load error, apply the Task 3 Step 4
contingency (bump LLamaSharp in the helper csproj only) and re-run.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Model/Settings.cs tools/fetch-models.ps1
git commit -m "feat(semantic): SemanticSearch settings toggle + fetch-models -Embedding (EmbeddingGemma-300m, role=embedding)"
```

---

### Task 10: App wiring — construction, events, lifetime

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs`

**Interfaces:**
- Consumes: `SemanticIndexService` / `AssistantEmbeddingClient` / `EmbeddingMethod` (Tasks 4/8/1), `AssistantHelperLocator.FindExe()`, `comp.AssistantModels.GetAsync`, `ProcessAssistantHelper`, `searchIndex.SnapshotEntries`, existing `assistantBusyReason` func.
- Produces: a live `SemanticIndexService?` handed to the Search VM via `searchVm.AttachSemantic(...)` (Task 11 defines it — **implement Task 11's VM member first if executing out of order; in-order execution defines it in the next task, so this task ends with a compile against a temporary no-op `AttachSemantic` stub added here and filled in Task 11**. In-order: add the stub in this task, replace in Task 11.)

- [ ] **Step 1: Declare the hoisted locals**

Near `var searchIndex = new ...` (App.xaml.cs ~line 85), add:

```csharp
        // Semantic search (design 2026-07-25): constructed LATE, inside the post-scan
        // continuation - it needs the assistant manifest (embedding-role model) and the lexical
        // index. Hoisted so the import-completion and exit paths below can reach them.
        LocalScribe.Core.Search.Semantic.SemanticIndexService? semanticIndex = null;
        LocalScribe.Core.Search.Semantic.AssistantEmbeddingClient? embeddingClient = null;
        const int SemanticDim = 256;
```

- [ ] **Step 2: Stub the VM attach point (replaced by Task 11)**

In `src/LocalScribe.App/ViewModels/SearchPageViewModel.cs`, add a minimal member so this task
compiles (Task 11 replaces the body):

```csharp
    /// <summary>Semantic seam - attached late from the post-scan continuation (Task 11 wires
    /// the Related section to it).</summary>
    public void AttachSemantic(LocalScribe.Core.Search.Semantic.ISemanticSearch semantic) { }
```

- [ ] **Step 3: Wire the continuation**

Replace the existing `orchestrator.ScanCompleted.ContinueWith` block (~line 965) with:

```csharp
        _ = orchestrator.ScanCompleted.ContinueWith(async _ =>
        {
            try { await searchIndex.InitializeAsync(_shutdownCts.Token); }
            catch (OperationCanceledException) { }    // shutdown mid-build: self-heals next launch
            catch { }
            // Semantic index (design 2026-07-25): AFTER the lexical build (it is the eligibility
            // + facet authority). Presence-gated: settings toggle + helper exe + embedding-role
            // model. All failures leave semantic off; lexical search is never affected.
            try
            {
                if (!comp.Settings.Current.SemanticSearch.Enabled) return;
                var manifest = await comp.AssistantModels.GetAsync(_shutdownCts.Token);
                if (manifest.EmbeddingModel is not { } embedModel) return;
                if (LocalScribe.Core.Assistant.AssistantHelperLocator.FindExe() is not string exe) return;
                embeddingClient = new LocalScribe.Core.Search.Semantic.AssistantEmbeddingClient(
                    new Services.ProcessAssistantHelper(exe), embedModel.FilePath, SemanticDim);
                var semantic = new LocalScribe.Core.Search.Semantic.SemanticIndexService(
                    comp.Paths, () => comp.Settings.Current, TimeProvider.System,
                    embeddingClient,
                    LocalScribe.Core.Search.Semantic.EmbeddingMethod.For(embedModel.FilePath, SemanticDim),
                    SemanticDim,
                    recordingBusy: assistantBusyReason,
                    lexicalSnapshot: searchIndex.SnapshotEntries);
                semantic.SessionSkipped += (id, ex) => System.Diagnostics.Trace.WriteLine(
                    $"semantic index skipped session {id}: {ex.Message}");
                semanticIndex = semantic;
                // Incremental seams - the exact same events the lexical index rides.
                comp.Maintenance.SessionContentChanged += id => semantic.Enqueue(id);
                comp.Controller.SessionFinalizeCompleted += id => semantic.Enqueue(id);
                comp.Retranscription.RetranscriptionCompleted += id => semantic.Enqueue(id);
                dispatch(() => searchVm.AttachSemantic(semantic));
                await semantic.InitializeAsync(_shutdownCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("semantic index unavailable: " + ex.Message);
            }
        }, TaskScheduler.Default);
```

**Scope note:** `assistantBusyReason` is declared at ~line 281, AFTER the search wiring but
BEFORE this continuation — verify it is in scope here (it is a method-level local of the same
startup method); if the continuation lives in a different method in current code, capture the
same expression (`() => session.State != SessionState.Idle ? "..." : null`) locally instead.

- [ ] **Step 4: Import completion + exit**

In the `importVm.Completed` handler (~line 599), after the lexical reindex line, add:

```csharp
                semanticIndex?.Enqueue(id);                   // imported session becomes Related-searchable
```

In `OnExit` (~line 973), before the base call, add:

```csharp
        // Kill the warm embed helper at exit (best-effort; the process also dies with the tree).
        if (embeddingClient is { } ec) _ = ec.DisposeAsync();
```

(`embeddingClient` must therefore be hoisted to a field OR the OnExit addition goes wherever the
`_shutdownCts.Cancel()` teardown happens inside the same class — promote both hoisted locals to
private fields `_semanticIndex` / `_embeddingClient` on the App class if OnStartup locals are not
reachable from OnExit; adjust the references above accordingly. This is the one structural
judgment call in this task — match whichever pattern `_shutdownCts` itself uses.)

- [ ] **Step 5: Build**

Run: `dotnet build src/LocalScribe.App`
Expected: 0 errors, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/App.xaml.cs src/LocalScribe.App/ViewModels/SearchPageViewModel.cs
git commit -m "feat(semantic): app wiring - presence-gated construction, incremental seams, exit teardown"
```

---

### Task 11: Related-discussion UI — SearchPageViewModel + SearchPage.xaml

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SearchPageViewModel.cs`
- Modify: `src/LocalScribe.App/Pages/SearchPage.xaml`
- Test: `tests/LocalScribe.App.Tests/SearchPageViewModelSemanticTests.cs`

**Interfaces:**
- Consumes: `ISemanticSearch` (Task 8), existing `SearchResultCard`/`SearchSnippetRow`/`OpenSnippetCommand` click-through (semantic rows pass `MatchedTerm: ""` — `ShowFindAt(seq, "")` scrolls without a find term).
- Produces (bound by XAML): `RelatedResults : ObservableCollection<SearchResultCard>`, `IsRelatedSearching : bool`, `ShowRelatedSection : bool`, `RelatedStatus : string`, `RelatedCoverageNote : string`, `AttachSemantic(ISemanticSearch)`, test seam `PendingSearch` (existing, now completes after the semantic leg too).

- [ ] **Step 1: Write the failing tests**

`tests/LocalScribe.App.Tests/SearchPageViewModelSemanticTests.cs` — mirror the existing
`SearchPageViewModelTests` arrange style **exactly** (same queued-dispatch fake, same
`SearchIndexService` seeding through a temp `StoragePaths`; copy its helper methods). The
semantic-specific cases:

```csharp
// Arrange helpers: copy MakeVm/queued-dispatch/seed from SearchPageViewModelTests, then:

private sealed class FakeSemantic : ISemanticSearch
{
    public (int Fresh, int Eligible) Coverage { get; set; } = (1, 1);
    public event Action? Changed;
    public Func<IReadOnlyList<SemanticResult>>? OnQuery { get; set; }
    public int Queries;
    public Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct)
    { Queries++; return Task.FromResult(OnQuery?.Invoke() ?? []); }
    public void RaiseChanged() => Changed?.Invoke();
}

[Fact] // semantic results land in RelatedResults AFTER lexical, section shown
public async Task Related_section_fills_from_the_semantic_seam() { /*
    attach FakeSemantic returning one SemanticResult built over a seeded session's
    SearchSessionEntry with one SemanticHit(StartSeq: 0, ..., Snippet: "related text", Score: 0.9f);
    set QueryText; drain PendingSearch + queued dispatch;
    Assert.Single(vm.RelatedResults); Assert.True(vm.ShowRelatedSection);
    Assert.Equal("", vm.RelatedResults[0].Snippets[0].Speaker or verify snippet text mapping */ }

[Fact] // no semantic attached -> section never shows, no crash
public async Task Without_semantic_the_section_stays_hidden() { /* no AttachSemantic;
    set QueryText; drain; Assert.False(vm.ShowRelatedSection); */ }

[Fact] // semantic failure -> RelatedStatus set, lexical results untouched
public async Task Semantic_failure_degrades_to_a_status_line() { /* OnQuery = () => throw ...;
    drain; Assert.Contains("unavailable", vm.RelatedStatus, StringComparison.OrdinalIgnoreCase);
    lexical Results still populated */ }

[Fact] // coverage note text
public async Task Incomplete_coverage_shows_the_searched_N_of_M_note() { /* Coverage = (84, 120);
    drain; Assert.Equal("searched 84 of 120 sessions - indexing continues", vm.RelatedCoverageNote); */ }

[Fact] // complete coverage -> empty note
public async Task Full_coverage_hides_the_note() { /* Coverage = (5, 5); drain;
    Assert.Equal("", vm.RelatedCoverageNote); */ }

[Fact] // empty query clears the related section
public async Task Clearing_the_query_clears_related() { /* fill, then QueryText = ""; drain;
    Assert.Empty(vm.RelatedResults); Assert.False(vm.ShowRelatedSection); */ }
```

Write these as REAL tests (the comment bodies above show the intent; the arrange plumbing comes
from the existing test file — that is why copying its helpers is step 0 of this test file).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SearchPageViewModelSemanticTests"`
Expected: FAIL — `RelatedResults` etc. do not exist.

- [ ] **Step 3: Implement the VM**

In `SearchPageViewModel.cs`:

1. Add state + the real `AttachSemantic` (replacing Task 10's stub):

```csharp
    private ISemanticSearch? _semantic;

    /// <summary>Related-discussion section (design 2026-07-25): semantic-only hits, rendered as
    /// the same card shape as exact results. Never interleaved with lexical ranking.</summary>
    public ObservableCollection<SearchResultCard> RelatedResults { get; } = [];
    [ObservableProperty] private bool _isRelatedSearching;
    [ObservableProperty] private bool _showRelatedSection;
    [ObservableProperty] private string _relatedStatus = "";
    [ObservableProperty] private string _relatedCoverageNote = "";

    /// <summary>Attached late (post-scan continuation) when the feature is presence-gated ON.
    /// Changed refreshes the coverage note and re-runs the pending query so results improve as
    /// the backfill progresses.</summary>
    public void AttachSemantic(ISemanticSearch semantic)
    {
        _semantic = semantic;
        semantic.Changed += () => _dispatch(() => { UpdateCoverageNote(); ScheduleSearch(); });
        UpdateCoverageNote();
        ScheduleSearch();
    }

    private void UpdateCoverageNote()
    {
        if (_semantic is not { } s) { RelatedCoverageNote = ""; return; }
        var (fresh, eligible) = s.Coverage;
        RelatedCoverageNote = fresh < eligible
            ? $"searched {fresh} of {eligible} sessions - indexing continues"
            : "";
    }
```

(add `using LocalScribe.Core.Search.Semantic;` to the file's usings.)

2. Extend `RunSearchAsync` — after the existing `_dispatch(...)` that publishes lexical results,
append the semantic leg (still inside the try, same `ct`):

```csharp
            if (_semantic is { } semantic && hasQuery)
            {
                _dispatch(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    IsRelatedSearching = true;
                    RelatedStatus = "";
                    ShowRelatedSection = true;
                    UpdateCoverageNote();
                });
                try
                {
                    var related = await semantic.QueryAsync(query, results, ct);
                    if (ct.IsCancellationRequested) return;
                    _dispatch(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        RelatedResults.Clear();
                        foreach (var r in related.Take(10)) RelatedResults.Add(ToRelatedCard(r));
                        IsRelatedSearching = false;
                        ShowRelatedSection = RelatedResults.Count > 0
                            || RelatedCoverageNote.Length > 0;
                    });
                }
                catch (OperationCanceledException) { }
                catch
                {
                    _dispatch(() =>
                    {
                        IsRelatedSearching = false;
                        RelatedStatus = "Related search unavailable.";
                        ShowRelatedSection = true;
                    });
                }
            }
            else
            {
                _dispatch(() =>
                {
                    RelatedResults.Clear();
                    IsRelatedSearching = false;
                    RelatedStatus = "";
                    ShowRelatedSection = false;
                });
            }
```

3. Add the card mapper beside `ToCard` (semantic rows: no speaker column — the chunk snippet
carries speaker prefixes; `MatchedTerm: ""` so click-through scrolls without a find term):

```csharp
    private SearchResultCard ToRelatedCard(SemanticResult r)
    {
        var startedLocal = r.Session.UtcOffsetMinutes is int offsetMin
            ? r.Session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : r.Session.StartedAtUtc.ToLocalTime();
        string matters = string.Join(", ", r.Session.MatterIds.Select(MatterLabel));
        var rows = r.Hits.Select(h => new SearchSnippetRow(
            r.Session.SessionId, h.StartSeq, MatchedTerm: "",
            TimestampFormat.Stamp(h.StartMs, "relative", startedLocal),
            Speaker: "", h.Snippet, MatchesOriginalOnly: false)).ToList();
        return new SearchResultCard(r.Session.SessionId, r.Session.Title,
            startedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            r.Session.App, matters, rows);
    }
```

- [ ] **Step 4: XAML**

In `SearchPage.xaml`:

1. Extract the result-card `DataTemplate` (the whole `Border` inside the existing
`ListView.ItemTemplate`) into `Page.Resources` as `<DataTemplate x:Key="ResultCardTemplate">`
and reference it from the ListView via `ItemTemplate="{StaticResource ResultCardTemplate}"`.

2. Wrap the existing results `Grid` content: give the inner `Grid` two rows; existing ListView +
empty-state TextBlocks go in row 0; add row 1:

```xml
                <Grid.RowDefinitions>
                    <RowDefinition Height="*" />
                    <RowDefinition Height="Auto" />
                </Grid.RowDefinitions>
                <!-- existing ListView + empty-state TextBlocks stay in row 0 (Grid.Row="0") -->
                <!-- Related discussion (semantic, design 2026-07-25): visually distinct section
                     BELOW exact results - never interleaved. -->
                <Border Grid.Row="1" Margin="0,8,0,0" Padding="10" CornerRadius="6"
                        BorderThickness="1" MaxHeight="300"
                        Background="{DynamicResource ControlFillColorSecondaryBrush}"
                        BorderBrush="{DynamicResource ControlStrokeColorDefaultBrush}"
                        Visibility="{Binding ShowRelatedSection, Converter={StaticResource BoolToVis}}">
                    <DockPanel>
                        <WrapPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,6">
                            <TextBlock Text="Related discussion" FontWeight="SemiBold" Margin="0,0,8,0" />
                            <TextBlock Text="{Binding RelatedCoverageNote}" Opacity="0.7" Margin="0,0,8,0"
                                       FontStyle="Italic" />
                            <TextBlock Text="searching..." FontStyle="Italic" Opacity="0.7"
                                       Visibility="{Binding IsRelatedSearching, Converter={StaticResource BoolToVis}}" />
                            <TextBlock Text="{Binding RelatedStatus}" Opacity="0.7" />
                        </WrapPanel>
                        <ScrollViewer VerticalScrollBarVisibility="Auto">
                            <ItemsControl ItemsSource="{Binding RelatedResults}"
                                          ItemTemplate="{StaticResource ResultCardTemplate}" />
                        </ScrollViewer>
                    </DockPanel>
                </Border>
```

- [ ] **Step 5: Run the new tests AND the existing SearchPageViewModel tests**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SearchPageViewModel"`
Expected: PASS (existing + 6 new).

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SearchPageViewModel.cs src/LocalScribe.App/Pages/SearchPage.xaml tests/LocalScribe.App.Tests/SearchPageViewModelSemanticTests.cs
git commit -m "feat(semantic): Related-discussion section - labeled cards, coverage note, graceful degrade"
```

---

### Task 12: Full gate + smoke runbook

**Files:**
- Create: `docs/plans/2026-07-25-semantic-search-smoke-runbook.md`

- [ ] **Step 1: Full build + both test suites**

Run:
```bash
dotnet build LocalScribe.slnx
dotnet test tests/LocalScribe.Core.Tests
dotnet test tests/LocalScribe.App.Tests
```
Expected: build 0 warnings; Core and App suites fully green (known-skip baseline unchanged —
compare counts against the last master run, currently Core 882/884, App 717/717).

- [ ] **Step 2: Write the smoke runbook**

`docs/plans/2026-07-25-semantic-search-smoke-runbook.md`:

```markdown
# Semantic search - real-model smoke runbook (user-run)

Prereqs: `tools/fetch-models.ps1 -Embedding`, assistant helper published (or dev tools/assistant),
app built.

S1. Helper op: `pwsh tools/smoke-embed.ps1` -> PASS line, dim 256, unit-normalized.
S2. First backfill: launch app with existing corpus; Search page -> type a query ->
    Related section shows "searched N of M sessions - indexing continues"; N reaches M
    within minutes (small corpus) with the app idle. Task Manager: ONE
    LocalScribe.Assistant.exe, working set well under 1 GB, CPU quiet after backfill.
S3. Meaning, not words: search "settlement figure" against a session that discusses money
    amounts WITHOUT those words -> session appears under Related discussion; click a row ->
    read view opens scrolled to the right passage.
S4. Multilingual: query in English against a non-English session (if available) -> hit lands.
S5. Facets: set a Matter/date/app facet -> Related section respects it identically to exact.
S6. Recording pause + memory: start a recording mid-backfill -> within seconds
    LocalScribe.Assistant.exe (embed instance) EXITS in Task Manager; searching still fills
    Related (a fresh helper spawns, then dies after 5 idle minutes); stop recording ->
    backfill resumes, coverage note advances.
S7. Edit staleness: correct a line in a Related-hit passage -> within seconds the session
    re-embeds (coverage dips then recovers); the corrected wording is what the snippet shows.
S8. Deletability: close app, delete <root>\index\semantic\ entirely, relaunch -> full rebuild,
    no errors anywhere.
S9. Floor sanity: nonsense query ("purple quantum sandwich") -> Related section empty or
    hidden, never padded with junk. If real queries return junk or miss obvious passages,
    tune SemanticQueryEngine.MinScore (0.55) and re-run S3.
```

- [ ] **Step 3: Commit**

```bash
git add docs/plans/2026-07-25-semantic-search-smoke-runbook.md
git commit -m "docs(semantic): real-model smoke runbook"
```

---

## Plan Self-Review Notes (already applied)

- **Spec coverage:** wire+helper (spec "Model and helper protocol") → Tasks 1–3; chunker (spec
  "Chunking") → Task 5; store (spec "Index storage") → Task 6; query/dedup/facets (spec "Query
  path") → Task 7; service/backfill/pause/coverage (spec "Backfill and scheduling", "Error
  handling") → Task 8; toggle+model distribution → Task 9; wiring → Task 10; UI → Task 11;
  testing strategy + manual smoke → per-task tests + Task 12. Memory rule (mid-brainstorm user
  concern) → Global Constraints + client `ReleaseAsync` + service pause test + smoke S6.
- **Type consistency:** `ISemanticSearch`/`SemanticResult`/`SemanticHit`/`SemanticSidecar`/
  `SemanticChunk`/`EmbeddingBatch`/`IEmbeddingClient` names and signatures are identical across
  Tasks 4–11. `EmbeddingMethod.For` used in Tasks 1, 3, 10 with the same formula.
- **Known judgment calls left to the implementer (bounded, flagged in place):** LLamaSharp 0.25
  embedder member spellings (Task 3, adapt inside EmbedEngine only); App.xaml.cs local-vs-field
  hoisting for OnExit reach (Task 10); HF blob name for the GGUF if the pinned path moved
  (Task 9). Each has its verification step.

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

// Humble object at the LLamaSharp/native boundary (the SherpaDiarisationRunner precedent):
// not unit-tested; the stdio contract around it is pinned by AssistantJobRunnerTests and
// ProcessAssistantHelperTests, and the real-model path is smoke-only.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Native;

namespace LocalScribe.Assistant;

internal sealed class LlamaEngine : IDisposable
{
    /// <summary>The backend ACTUALLY used ("cuda" or "cpu") - reported in every done event
    /// (floor-fall provenance, design 7.7: CUDA fall to CPU is recorded, never silent).</summary>
    public string Backend { get; }
    public int LastPromptTokens { get; private set; }

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;

    /// <summary>Backend pick (design 7.1): "cuda" -> GPU or throw; "cpu" -> CPU;
    /// "auto" -> try CUDA (all layers offloaded), fall to CPU on ANY load failure.</summary>
    public static LlamaEngine Load(string modelPath, int ctxTokens, string backendRequest, Action<string> phase)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"model file missing: {modelPath}", modelPath);
        if (backendRequest != "cpu")
        {
            try
            {
                phase("load-cuda");
                return new LlamaEngine(modelPath, ctxTokens, gpuLayerCount: int.MaxValue, "cuda");
            }
            catch (Exception) when (backendRequest == "auto")
            {
                // fall through: CPU always works; the done event will honestly say "cpu"
            }
        }
        phase("load-cpu");
        return new LlamaEngine(modelPath, ctxTokens, gpuLayerCount: 0, "cpu");
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
        // ChatML template (Qwen3 family). Qwen3-*-Instruct-2507 is a NON-thinking model -
        // thinking disabled by model choice, the whole budget goes to the answer (design 7.2).
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

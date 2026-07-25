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

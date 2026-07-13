using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

public class ModelFileResolverTests
{
    // ---------- Candidate order (spec 3: quantized weights on CPU/iGPU, fp16 on CUDA) ----------
    // Descending fidelity on CPU/Vulkan (q8_0 near-lossless first - evidentiary transcripts);
    // CUDA puts plain f16 first. Review finding (2026-07-13): the candidate list and
    // CanonicalName MUST cover the same suffix set, or a disk holding only e.g. a q5_0 file
    // advertises a model Start accepts but the factory cannot load (audio-only husk session).
    // HF ggerganov/whisper.cpp ships medium/large quantized ONLY as q5_0.

    [Theory]
    [InlineData(Backend.Cpu)]
    [InlineData(Backend.Vulkan)]
    public void Cpu_and_vulkan_prefer_quantized_by_fidelity_then_plain(Backend backend)
        => Assert.Equal(
            new[]
            {
                "ggml-small.en-q8_0.bin", "ggml-small.en-q5_1.bin", "ggml-small.en-q5_0.bin",
                "ggml-small.en-q4_1.bin", "ggml-small.en-q4_0.bin", "ggml-small.en.bin",
            },
            ModelFileResolver.CandidateFiles(backend, "small.en"));

    [Fact]
    public void Cuda_prefers_plain_f16_then_quantized_by_fidelity()
        => Assert.Equal(
            new[]
            {
                "ggml-small.en.bin", "ggml-small.en-q8_0.bin", "ggml-small.en-q5_1.bin",
                "ggml-small.en-q5_0.bin", "ggml-small.en-q4_1.bin", "ggml-small.en-q4_0.bin",
            },
            ModelFileResolver.CandidateFiles(Backend.Cuda, "small.en"));

    [Fact]
    public void Every_suffix_canonical_name_strips_is_a_resolve_candidate()
    {
        // The bijection that closes the husk-session hole: any file AvailableModels maps to a
        // canonical name MUST be loadable by Resolve for that name, on every backend.
        foreach (string quant in new[] { "q8_0", "q5_1", "q5_0", "q4_1", "q4_0" })
        {
            string file = $"ggml-medium.en-{quant}.bin";
            Assert.Equal("medium.en", ModelFileResolver.CanonicalName($"medium.en-{quant}"));
            foreach (var backend in new[] { Backend.Cpu, Backend.Vulkan, Backend.Cuda })
                Assert.Equal(file, ModelFileResolver.Resolve(backend, "medium.en",
                    new HashSet<string> { file }.Contains));
        }
    }

    // ---------- Resolution ----------

    [Fact]
    public void Resolves_first_existing_candidate()
    {
        var onDisk = new HashSet<string> { "ggml-small.en-q5_1.bin", "ggml-small.en.bin" };
        Assert.Equal("ggml-small.en-q5_1.bin",
            ModelFileResolver.Resolve(Backend.Cpu, "small.en", onDisk.Contains));
        Assert.Equal("ggml-small.en.bin",
            ModelFileResolver.Resolve(Backend.Cuda, "small.en", onDisk.Contains));
    }

    [Fact]
    public void Quantized_only_disk_still_resolves_on_cuda()
    {
        var onDisk = new HashSet<string> { "ggml-small.en-q8_0.bin" };
        Assert.Equal("ggml-small.en-q8_0.bin",
            ModelFileResolver.Resolve(Backend.Cuda, "small.en", onDisk.Contains));
    }

    [Fact]
    public void Nothing_on_disk_falls_back_to_the_plain_canonical_name()
    {
        // The plain name is what fetch-models documents, so Require's "not downloaded"
        // error stays actionable.
        Assert.Equal("ggml-small.en.bin",
            ModelFileResolver.Resolve(Backend.Cpu, "small.en", _ => false));
    }

    [Fact]
    public void Unknown_quant_style_suffix_stays_raw_and_resolves_verbatim()
    {
        // A suffix outside the known list (e.g. a future q9_9) is NOT canonicalized anywhere -
        // AvailableModels keeps the raw name, Select passes it through, and the plain candidate
        // IS the file, so the exotic weight loads verbatim exactly as it did pre-branch.
        Assert.Equal("small.en-q9_9", ModelFileResolver.CanonicalName("small.en-q9_9"));
        var onDisk = new HashSet<string> { "ggml-small.en-q9_9.bin" };
        Assert.Equal("ggml-small.en-q9_9.bin",
            ModelFileResolver.Resolve(Backend.Cpu, "small.en-q9_9", onDisk.Contains));
    }

    // ---------- Canonical model names (quant suffix is a file detail, not a model name) ----------
    // Review finding (2026-07-13): strips EXACTLY the suffixes CandidateFiles probes - the two
    // lists share one source of truth so canonicalized always implies resolvable.

    [Theory]
    [InlineData("small.en-q8_0", "small.en")]
    [InlineData("small.en-q5_1", "small.en")]
    [InlineData("medium.en-q5_0", "medium.en")]
    [InlineData("base-q4_1", "base")]
    [InlineData("base-q4_0", "base")]
    [InlineData("tiny.en", "tiny.en")]
    [InlineData("base", "base")]
    [InlineData("large-v3", "large-v3")]       // -v3 is a version, not a quant suffix
    [InlineData("small.en-q9_9", "small.en-q9_9")]   // unknown suffix: raw-name path, never stripped
    public void Canonical_name_strips_only_known_quant_suffixes(string raw, string expected)
        => Assert.Equal(expected, ModelFileResolver.CanonicalName(raw));
}

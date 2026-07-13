using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

public class ModelFileResolverTests
{
    // ---------- Candidate order (spec 3: quantized weights on CPU/iGPU, fp16 on CUDA) ----------
    // q8_0 preferred over q5_1 on CPU/Vulkan: near-lossless accuracy (evidentiary transcripts)
    // while still roughly halving memory traffic vs f16.

    [Theory]
    [InlineData(Backend.Cpu)]
    [InlineData(Backend.Vulkan)]
    public void Cpu_and_vulkan_prefer_quantized_then_plain(Backend backend)
        => Assert.Equal(
            new[] { "ggml-small.en-q8_0.bin", "ggml-small.en-q5_1.bin", "ggml-small.en.bin" },
            ModelFileResolver.CandidateFiles(backend, "small.en"));

    [Fact]
    public void Cuda_prefers_plain_f16_then_quantized()
        => Assert.Equal(
            new[] { "ggml-small.en.bin", "ggml-small.en-q8_0.bin", "ggml-small.en-q5_1.bin" },
            ModelFileResolver.CandidateFiles(Backend.Cuda, "small.en"));

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
    public void Hand_edited_explicit_quantized_model_name_resolves_verbatim()
    {
        // settings.json hand-edited to "small.en-q8_0": the double-suffix probes miss and
        // the plain candidate IS the quantized file - no special-casing required.
        var onDisk = new HashSet<string> { "ggml-small.en-q8_0.bin" };
        Assert.Equal("ggml-small.en-q8_0.bin",
            ModelFileResolver.Resolve(Backend.Cpu, "small.en-q8_0", onDisk.Contains));
    }

    // ---------- Canonical model names (quant suffix is a file detail, not a model name) ----------

    [Theory]
    [InlineData("small.en-q8_0", "small.en")]
    [InlineData("small.en-q5_1", "small.en")]
    [InlineData("tiny.en", "tiny.en")]
    [InlineData("base", "base")]
    [InlineData("large-v3", "large-v3")]   // -v3 is a version, not a quant suffix
    public void Canonical_name_strips_only_quant_suffixes(string raw, string expected)
        => Assert.Equal(expected, ModelFileResolver.CanonicalName(raw));
}

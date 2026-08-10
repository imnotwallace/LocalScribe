using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;
using Whisper.net.LibraryLoader;

namespace LocalScribe.Core.Tests;

/// <summary>THE DEFECT (2026-08-11): the Backend setting did not constrain the runtime at all.
/// All three hosts assigned RuntimeOptions.RuntimeLibraryOrder an unconditional
/// [Cuda, Vulkan, Cpu] literal, and BackendPlan.Backend was never passed to the loader - it only
/// picked the weights FILE (f16 vs quantized) and gated the CPU thread count. Choosing "cpu" on a
/// CUDA box therefore RECORDED "cpu" into session.json and the engine chip while whisper.cpp
/// happily ran CUDA. In a product whose exports carry a backend line as provenance, that is a
/// false record, not just a dead setting.
///
/// The constraint can only be applied ONCE PER PROCESS: Whisper.net documents that
/// RuntimeOptions only takes effect "before any WhisperFactory is created", and that once a
/// library is loaded it is used for all subsequent processing. So this maps the persisted setting
/// to a load order the host applies at startup - it is deliberately NOT re-applied per session,
/// because doing so would silently no-op after the first engine and look like it worked.
///
/// CPU is kept as the last resort for an explicit Cuda/Vulkan choice. "Recording always wins"
/// outranks honouring a picker: a machine whose GPU driver disappears must still transcribe. What
/// must never happen is the fall being SILENT, which is why the loaded runtime is reported
/// separately rather than assumed from this order.</summary>
public sealed class WhisperRuntimeOrderTests
{
    [Fact]
    public void Auto_probes_the_full_cascade()
    {
        Assert.Equal(
            new[] { RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
            WhisperRuntimeOrder.For(Backend.Auto));
    }

    [Fact]
    public void Cpu_excludes_every_gpu_runtime()
    {
        // The whole point: picking cpu previously left CUDA first in the order, so the setting
        // recorded a lie. Nothing GPU may appear here.
        var order = WhisperRuntimeOrder.For(Backend.Cpu);

        Assert.Equal(new[] { RuntimeLibrary.Cpu }, order);
    }

    [Fact]
    public void Cuda_excludes_vulkan()
    {
        var order = WhisperRuntimeOrder.For(Backend.Cuda);

        Assert.DoesNotContain(RuntimeLibrary.Vulkan, order);
        Assert.Equal(RuntimeLibrary.Cuda, order[0]);
    }

    [Fact]
    public void Vulkan_excludes_cuda()
    {
        var order = WhisperRuntimeOrder.For(Backend.Vulkan);

        Assert.DoesNotContain(RuntimeLibrary.Cuda, order);
        Assert.Equal(RuntimeLibrary.Vulkan, order[0]);
    }

    [Fact]
    public void An_explicit_gpu_choice_keeps_cpu_as_the_last_resort()
    {
        // Not a weakening of the constraint - a machine that loses its GPU driver must still
        // transcribe, because losing the recording is the worse failure. The fall is disclosed
        // separately; what this asserts is only that it remains POSSIBLE.
        Assert.Equal(RuntimeLibrary.Cpu, WhisperRuntimeOrder.For(Backend.Cuda)[^1]);
        Assert.Equal(RuntimeLibrary.Cpu, WhisperRuntimeOrder.For(Backend.Vulkan)[^1]);
    }

    [Fact]
    public void Every_order_ends_at_a_runtime_that_can_actually_load_somewhere()
    {
        // A load order with no CPU entry anywhere can leave whisper.cpp unable to load ANY
        // runtime, which fails the session rather than degrading it.
        foreach (Backend b in Enum.GetValues<Backend>())
            Assert.Contains(RuntimeLibrary.Cpu, WhisperRuntimeOrder.For(b));
    }
}

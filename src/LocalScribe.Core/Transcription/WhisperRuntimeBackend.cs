using LocalScribe.Core.Model;
using Whisper.net.LibraryLoader;

namespace LocalScribe.Core.Transcription;

/// <summary>What whisper.cpp ACTUALLY loaded, mapped to the <see cref="Backend"/> vocabulary the
/// record speaks (2026-08-11). Companion to <see cref="WhisperRuntimeOrder"/>: that one makes the
/// setting real, this one makes the RECORD real.
///
/// Until now `session.json`, the live engine chip and the export provenance line all carried the
/// REQUESTED backend, so an explicit CUDA pick on a machine where the CUDA runtime could not load
/// still exported "CUDA". The assistant already proves its GPU claim from llama.cpp's own load log
/// rather than asserting it; whisper gets the same treatment through
/// RuntimeOptions.LoadedLibrary.</summary>
public static class WhisperRuntimeBackend
{
    /// <summary>Null means **make no claim** - either nothing has loaded yet (no engine has been
    /// created, so there is no truth to record) or the runtime is one this build has no vocabulary
    /// for. Callers fall back to the requested plan rather than inventing a value, because a
    /// guessed backend in evidentiary data is exactly the defect this exists to remove.</summary>
    public static Backend? For(RuntimeLibrary? loaded) => loaded switch
    {
        RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12 => Backend.Cuda,
        RuntimeLibrary.Vulkan => Backend.Vulkan,
        // CpuNoAvx is still CPU to a reader - the AVX split is a build variant, not a backend.
        RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => Backend.Cpu,
        _ => null,
    };

    /// <summary>Humble read of the process-wide loaded runtime (untestable static; the mapping
    /// above carries the logic). Meaningful only after the first engine has been created -
    /// Whisper.net populates it at load time and never changes it afterwards.</summary>
    public static Backend? Loaded => For(RuntimeOptions.LoadedLibrary);
}

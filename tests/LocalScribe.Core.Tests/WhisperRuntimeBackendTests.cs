using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;
using Whisper.net.LibraryLoader;

namespace LocalScribe.Core.Tests;

/// <summary>Reading the backend back off the runtime that actually loaded (2026-08-11), rather
/// than recording what was asked for. Constraining the load order (WhisperRuntimeOrder) made the
/// setting real; this makes the RECORD real. session.json, the live engine chip and the export
/// provenance line all carried the REQUESTED backend, so an explicit CUDA pick on a box where the
/// CUDA runtime could not load still exported "CUDA".
///
/// The assistant already set this precedent: its GPU claim is proved from llama.cpp's own load log
/// and a partial offload is recorded as a CPU fall. Whisper gets the same treatment via
/// RuntimeOptions.LoadedLibrary.</summary>
public sealed class WhisperRuntimeBackendTests
{
    [Theory]
    [InlineData(RuntimeLibrary.Cuda, Backend.Cuda)]
    [InlineData(RuntimeLibrary.Cuda12, Backend.Cuda)]   // a CUDA 12 build is still CUDA to a reader
    [InlineData(RuntimeLibrary.Vulkan, Backend.Vulkan)]
    [InlineData(RuntimeLibrary.Cpu, Backend.Cpu)]
    [InlineData(RuntimeLibrary.CpuNoAvx, Backend.Cpu)]  // still CPU; the AVX split is not a backend
    public void Maps_a_loaded_runtime_to_the_backend_a_reader_would_recognise(
        RuntimeLibrary loaded, Backend expected)
    {
        Assert.Equal(expected, WhisperRuntimeBackend.For(loaded));
    }

    [Fact]
    public void Nothing_loaded_yet_is_not_a_backend_claim()
    {
        // Before the first engine exists there is no truth to record, and inventing one would be
        // the very failure this replaces. Callers fall back to the requested plan and say so.
        Assert.Null(WhisperRuntimeBackend.For(null));
    }

    [Fact]
    public void An_unrecognised_runtime_makes_no_claim_rather_than_guessing()
    {
        // CoreML/OpenVino cannot load on the Windows-only build, but a future Whisper.net could add
        // a member. Mapping an unknown runtime onto one of our four would put a false value into
        // evidentiary data; null means "record what was requested, and do not assert".
        Assert.Null(WhisperRuntimeBackend.For(RuntimeLibrary.CoreML));
        Assert.Null(WhisperRuntimeBackend.For(RuntimeLibrary.OpenVino));
    }
}

using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Core.Tests;

/// <summary>What goes into session.json / TranscriptVersion for the backend (2026-08-11).
///
/// `backend` means WHAT RAN, read off RuntimeOptions.LoadedLibrary. `backendRequested` records what
/// LocalScribe asked for, and is written ONLY when the two disagree - so the overwhelmingly common
/// session (the runtime that loaded is the one that was asked for) serialises exactly as it did
/// before, while a divergence that used to be invisible now appears in the record.
///
/// The pre-2026-08-11 behaviour recorded the request alone, so an explicit CUDA pick on a machine
/// where the CUDA runtime could not load exported "CUDA" regardless. This must not lose the
/// opposite fact either: the worker's mid-session floor-fall (a deliberate earlier fix, because a
/// same-file CUDA->CPU fall leaves no weights-changed marker) is exactly a case where the request
/// and the loaded runtime diverge, and it stays recorded.</summary>
public sealed class BackendRecordTests
{
    [Fact]
    public void Agreement_records_one_value_and_claims_nothing_extra()
    {
        var r = BackendRecord.For(requested: Backend.Cuda, loaded: Backend.Cuda);

        Assert.Equal("CUDA", r.Backend);
        Assert.Null(r.Requested);   // omitted on the wire - byte-identical to the old output
    }

    [Fact]
    public void The_loaded_runtime_wins_and_the_request_is_kept_beside_it()
    {
        // The defect: asked for CUDA, CUDA could not load, CPU ran - and the record said CUDA.
        var r = BackendRecord.For(requested: Backend.Cuda, loaded: Backend.Cpu);

        Assert.Equal("CPU", r.Backend);
        Assert.Equal("CUDA", r.Requested);
    }

    [Fact]
    public void A_mid_session_floor_fall_still_leaves_both_facts_in_the_record()
    {
        // The worker fell to CPU while the CUDA runtime stayed loaded - the library cannot be
        // unloaded mid-process. Recording only one of these would destroy information the earlier
        // floor-fall fix deliberately added.
        var r = BackendRecord.For(requested: Backend.Cpu, loaded: Backend.Cuda);

        Assert.Equal("CUDA", r.Backend);
        Assert.Equal("CPU", r.Requested);
    }

    [Fact]
    public void With_nothing_loaded_the_request_is_recorded_and_not_contradicted()
    {
        // No engine was ever created (a session that transcribed nothing, or a crash before the
        // first load). There is no runtime truth to record, so the request stands alone rather
        // than being reported as a divergence.
        var r = BackendRecord.For(requested: Backend.Vulkan, loaded: null);

        Assert.Equal("VULKAN", r.Backend);
        Assert.Null(r.Requested);
    }
}

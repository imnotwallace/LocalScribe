using LocalScribe.Core.Model;

namespace LocalScribe.Core.Transcription;

/// <summary>Composes the backend pair written into `session.json` and each `TranscriptVersion`
/// (2026-08-11).
///
/// `backend` is WHAT RAN - read off the runtime that whisper.cpp actually loaded
/// (<see cref="WhisperRuntimeBackend"/>). Before this, every write site recorded what was
/// REQUESTED, so an explicit CUDA pick on a machine where the CUDA runtime could not load still
/// exported "CUDA". A provenance line that ships inside an evidentiary export has to be a
/// measurement, not a restatement of the request.
///
/// `backendRequested` is written ONLY on a divergence. Two reasons: the common session (the
/// runtime that loaded is the one asked for) then serialises exactly as it did before, so no
/// existing record or export changes shape; and a value that appears only when it carries
/// information is far harder to misread than one that is always present and usually redundant.
///
/// Divergence is not a synonym for failure. The worker's mid-session floor-fall drops the PLAN to
/// CPU while the loaded library stays CUDA - it cannot be unloaded mid-process - and that fall was
/// deliberately made persistent by an earlier fix, because a same-file CUDA to CPU fall leaves no
/// "transcription weights changed" marker behind. Recording only one side of the pair would throw
/// that away, which is why both survive here.</summary>
public static class BackendRecord
{
    /// <param name="requested">What LocalScribe configured - the worker's effective plan backend,
    /// which already accounts for a mid-session floor-fall.</param>
    /// <param name="loaded">The runtime that actually loaded, or null when none has (no engine was
    /// ever created, so there is nothing to measure).</param>
    public static (string Backend, string? Requested) For(Backend requested, Backend? loaded)
    {
        string asked = requested.ToString().ToUpperInvariant();
        if (loaded is not { } ran) return (asked, null);       // nothing loaded: no claim to make

        string actual = ran.ToString().ToUpperInvariant();
        return (actual, actual == asked ? null : asked);
    }
}

// src/LocalScribe.Core/Audio/ICaptureHealthObservable.cs
namespace LocalScribe.Core.Audio;

/// <summary>Optional capability of a capture source that can report its own death (Tier 1B design
/// 2026-08-05, T1-4a). A SEPARATE interface probed with a type test, not a new member on
/// ICaptureSource: that interface has four implementations plus four test wrappers and widening it
/// would touch all of them - the same reason IEndpointMuteObservable exists and is probed as
/// `if (micSource is not IEndpointMuteObservable m) return;` (SessionController.cs:361).
///
/// The frame-arrival watchdog is the BACKSTOP and works for every source; this is the FAST path for
/// the one source that can actually tell us. Events may fire on arbitrary (WASAPI callback)
/// threads; consumers marshal - the same contract as FrameAvailable and DeviceMuteChanged.</summary>
public interface ICaptureHealthObservable
{
    /// <summary>Raised when the underlying capture stream has stopped on its own. The argument is
    /// the driver-supplied exception when there was one, and null for an ordinary stop (which every
    /// consumer must therefore ignore while it is deliberately stopping a leg).</summary>
    event Action<Exception?>? CaptureStopped;
}

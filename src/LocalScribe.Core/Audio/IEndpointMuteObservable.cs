// src/LocalScribe.Core/Audio/IEndpointMuteObservable.cs
namespace LocalScribe.Core.Audio;

/// <summary>Optional capability of a capture source whose ENDPOINT (device master) mute is
/// observable (design 2026-07-10 section 2). Device mute silences every client of the endpoint -
/// including LocalScribe's own stream - so the user must learn of it instantly, not after the
/// 15s silent-leg grace. Events may fire on arbitrary (COM callback) threads; consumers marshal.</summary>
public interface IEndpointMuteObservable
{
    bool DeviceMuted { get; }
    event Action<bool>? DeviceMuteChanged;
}

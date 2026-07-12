using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>The controller's hardware seam: creates fresh capture sources per leg (a fresh
/// MicCaptureSource re-resolves the default device on Resume) plus the honest device snapshot
/// for session.json (spec 1.2/12). Tests substitute fakes.</summary>
public interface ICaptureSourceProvider
{
    (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock);
    (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock);

    /// <summary>Explicit-target variant (design 2026-07-12 section "Architecture 2"): builds a
    /// source for the REQUESTED remote target rather than whatever the ambient settings resolve to.
    /// Used by SessionController.SetRemoteCaptureAsync for the mid-recording hot-swap.</summary>
    (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock, RemoteSetting explicitSetting);
}

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
}

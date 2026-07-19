using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace LocalScribe.Core.Live;

public interface IAudioSessionScanner
{
    IReadOnlyList<AudioSessionInfo> Scan();
}

/// <summary>Thin adapter (Humble Object): enumerates ACTIVE render audio sessions across ALL
/// active render endpoints (meeting apps often render to the Communications device, not the
/// Multimedia default). Validated by the Stage-1 spike; exercised live by the LiveRunner
/// smoke, not unit tests.</summary>
public sealed class WasapiSessionScanner : IAudioSessionScanner
{
    private readonly DataFlow _flow;

    /// <summary>Render endpoints - the original behavior for the remote-target picker/planner
    /// scan. Every existing caller uses this ctor and is behavior-identical.</summary>
    public WasapiSessionScanner() : this(DataFlow.Render) { }

    /// <summary>The same walk over the chosen endpoint direction. DataFlow.Capture enumerates the
    /// apps actively RECORDING from microphones - the call-detection signal (design 2026-07-18
    /// section 5.1): an allowlisted app opening the mic means a call is starting. One
    /// parameterized scanner instead of a parallel capture copy, so the two directions can never
    /// drift.</summary>
    public WasapiSessionScanner(DataFlow flow) => _flow = flow;

    public IReadOnlyList<AudioSessionInfo> Scan()
    {
        var enumerator = new MMDeviceEnumerator();
        var active = new List<AudioSessionInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(_flow, DeviceState.Active))
        {
            SessionCollection sessions;
            try { sessions = device.AudioSessionManager.Sessions; }
            catch { continue; }
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                uint pid;
                try { pid = s.GetProcessID; } catch { continue; }
                if (pid == 0) continue;
                string image;
                try { image = Process.GetProcessById((int)pid).ProcessName; }
                catch { continue; }                       // process may have just exited
                active.Add(new AudioSessionInfo(pid, image));
            }
        }
        return active;
    }
}

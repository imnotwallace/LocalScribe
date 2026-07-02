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
    public IReadOnlyList<AudioSessionInfo> Scan()
    {
        var enumerator = new MMDeviceEnumerator();
        var active = new List<AudioSessionInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
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

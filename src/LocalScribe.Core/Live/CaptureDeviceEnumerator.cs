using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>An active input (capture) endpoint: the WASAPI device Id (stable across sessions,
/// what a pin stores) plus the friendly name for display. Design section 1.</summary>
public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>Lists active capture endpoints for the mic pickers. Faked in VM tests; the real
/// implementation is thin over NAudio and smoke-verified (like WasapiSessionScanner).</summary>
public interface ICaptureDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> ListInputDevices();
}

/// <summary>Thin adapter (Humble Object) over NAudio: enumerates ACTIVE capture endpoints and
/// projects each to AudioDeviceInfo(d.ID, d.FriendlyName). Any enumeration failure returns an
/// EMPTY list (design section 1/7): the picker then offers only "follow default" and capture uses
/// the Communications default - it never crashes the Settings page or console. Exercised live by
/// the hardware smoke, not unit tests.</summary>
public sealed class WasapiCaptureDeviceEnumerator : ICaptureDeviceEnumerator
{
    public IReadOnlyList<AudioDeviceInfo> ListInputDevices()
    {
        try
        {
            var result = new List<AudioDeviceInfo>();
            foreach (var d in new MMDeviceEnumerator()
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                result.Add(new AudioDeviceInfo(d.ID, d.FriendlyName));
            return result;
        }
        catch
        {
            return [];   // no devices / enumeration failed -> follow-default only
        }
    }
}

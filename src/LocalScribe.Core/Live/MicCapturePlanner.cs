using LocalScribe.Core.Model;
namespace LocalScribe.Core.Live;

/// <summary>The resolved mic capture decision (design section 2). Mode is the HONEST mode the
/// snapshot will record: Pinned only when the pinned device is actually present; otherwise
/// FollowDefault. FellBackToDefault is true only when a real pin was requested but its device is
/// absent - that drives the spec section 12 "pinned microphone unavailable -> default" marker.</summary>
public sealed record MicPlan(MicMode Mode, string? DeviceId, bool FellBackToDefault);

/// <summary>Pure pin resolution (design section 2), the mic twin of RemoteCapturePlanner. Given
/// the saved MicSetting and the live capture-device list, decides whether to open a device by Id,
/// fall back to the Communications default, and whether that fall-back happened. No hardware, no
/// NAudio - fully unit-tested; WasapiCaptureSourceProvider.CreateMic applies the result.</summary>
public static class MicCapturePlanner
{
    public static MicPlan Plan(MicSetting mic, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id))
        {
            bool present = devices.Any(d => d.Id == mic.Id);
            return present
                ? new MicPlan(MicMode.Pinned, mic.Id, FellBackToDefault: false)
                : new MicPlan(MicMode.FollowDefault, null, FellBackToDefault: true);
        }
        return new MicPlan(MicMode.FollowDefault, null, FellBackToDefault: false);
    }
}

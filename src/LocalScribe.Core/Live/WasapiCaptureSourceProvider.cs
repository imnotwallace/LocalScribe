using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>Thin adapter (Humble Object) over the real WASAPI sources. Re-plans on every call (a
/// Resume leg re-scans / re-resolves the device). Settings resolve through the injected provider at
/// capture-plan time (design 6.2), so a settings save between sessions takes effect at the next
/// Start/Resume. The mic now honors a pinned device (design section 2): MicCapturePlanner decides
/// from the live capture-device list whether to open by Id or fall back to the Communications
/// default, and the snapshot records what actually happened (incl. FellBackToDefault).</summary>
public sealed class WasapiCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly Func<Settings> _settings;
    private readonly IAudioSessionScanner _scanner;
    private readonly ICaptureDeviceEnumerator _devices;

    public WasapiCaptureSourceProvider(Func<Settings> settingsProvider, IAudioSessionScanner scanner,
        ICaptureDeviceEnumerator? deviceEnumerator = null)
    {
        _settings = settingsProvider;
        _scanner = scanner;
        _devices = deviceEnumerator ?? new WasapiCaptureDeviceEnumerator();
    }

    /// <summary>Convenience overload: a fixed Settings snapshot (pre-Stage-4 call sites/tests).</summary>
    public WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)
        : this(() => settings, scanner)
    {
    }

    public (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock)
    {
        var plan = MicCapturePlanner.Plan(_settings().Mic, _devices.ListInputDevices());
        var mic = plan.Mode == MicMode.Pinned
            ? new MicCaptureSource(clock, plan.DeviceId!)
            : new MicCaptureSource(clock, Role.Communications);
        return (mic, new MicSnapshot
        {
            Mode = plan.Mode,
            Id = plan.Mode == MicMode.Pinned ? mic.DeviceId : null,
            Name = mic.DeviceName,
            FellBackToDefault = plan.FellBackToDefault,
        });
    }

    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)
    {
        var plan = RemoteCapturePlanner.Plan(_scanner.Scan(), _settings().Remote);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
}

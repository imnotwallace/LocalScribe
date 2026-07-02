using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>Thin adapter (Humble Object) over the real WASAPI sources. Re-plans the remote on
/// every call (a Resume leg re-scans - the meeting app may have changed); the caller snapshots
/// the FIRST leg's result into session.json. Pinned-mic mode is a Stage 7 concern: 3a always
/// follows the Communications default and records that honestly.</summary>
public sealed class WasapiCaptureSourceProvider : ICaptureSourceProvider
{
    private readonly Settings _settings;
    private readonly IAudioSessionScanner _scanner;

    public WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)
        => (_settings, _scanner) = (settings, scanner);

    public (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock)
    {
        var mic = new MicCaptureSource(clock, Role.Communications);
        return (mic, new MicSnapshot { Mode = MicMode.FollowDefault, Name = mic.DeviceName });
    }

    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)
    {
        var plan = RemoteCapturePlanner.Plan(_scanner.Scan(), _settings.Remote);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
}

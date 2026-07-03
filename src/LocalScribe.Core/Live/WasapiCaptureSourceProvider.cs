using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using NAudio.CoreAudioApi;
namespace LocalScribe.Core.Live;

/// <summary>Thin adapter (Humble Object) over the real WASAPI sources. Re-plans the remote on
/// every call (a Resume leg re-scans - the meeting app may have changed); the caller snapshots
/// the FIRST leg's result into session.json. Settings resolve through the injected provider at
/// capture-plan time (design 6.2): a settings save between sessions takes effect at the next
/// Start/Resume without rebuilding the provider. Pinned-mic mode is a Stage 7 concern: 3a
/// always follows the Communications default and records that honestly.</summary>
public sealed class WasapiCaptureSourceProvider(Func<Settings> settingsProvider,
    IAudioSessionScanner scanner) : ICaptureSourceProvider
{
    /// <summary>Convenience overload: a fixed Settings snapshot (pre-Stage-4 call sites/tests).</summary>
    public WasapiCaptureSourceProvider(Settings settings, IAudioSessionScanner scanner)
        : this(() => settings, scanner)
    {
    }

    public (ICaptureSource Source, MicSnapshot Snapshot) CreateMic(IClock clock)
    {
        var mic = new MicCaptureSource(clock, Role.Communications);
        return (mic, new MicSnapshot { Mode = MicMode.FollowDefault, Name = mic.DeviceName });
    }

    public (ICaptureSource Source, RemoteSnapshot Snapshot) CreateRemote(IClock clock)
    {
        var plan = RemoteCapturePlanner.Plan(scanner.Scan(), settingsProvider().Remote);
        ICaptureSource source = plan.Mode == RemoteMode.PerProcess
            ? new ProcessLoopbackCapture(plan.Pid!.Value, clock)
            : ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock);
        return (source, new RemoteSnapshot
        { Mode = plan.Mode, App = plan.App, FellBackToSystemMix = plan.FellBackToSystemMix });
    }
}

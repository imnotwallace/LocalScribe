// src/LocalScribe.SpikeRunner/Program.cs
//
// Stage 1 manual smoke harness: captures the microphone (Local) and the per-process
// loopback of a meeting app's render session (Remote) to two time-aligned 16 kHz mono WAVs.
// Webex-first: identifies the meeting app by IMAGE NAME among the ACTIVE RENDER sessions
// (Webex renders call audio in CiscoCollabHost.exe - a different PID from the UI), then
// activates per-process loopback on that PID with INCLUDE_TARGET_PROCESS_TREE.
//
// The Main thread is MTA (console default, no [STAThread]) so ProcessLoopbackCapture.Start()
// can block on the MTA-delivered ActivateCompleted callback without deadlock.
//
// Modes:
//   (default)              scan default app list, dual-capture to local.wav + remote.wav
//   <app> [<app> ...]      override the app-name match list
//   --system-loopback      Plan B: full-system loopback minus our own process tree
//   --activate-only <pid>  Task 9 gate: confirm activation succeeds for a specific render PID
//   --list                 Diagnostic: list active render sessions + capture (mic) devices
//   --mic-default          Record local.wav from the Multimedia default mic instead of the Communications default
//   --seconds <n>          Headless: record for n seconds instead of waiting for ENTER (also prints peak amplitude)

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using LocalScribe.Core.Audio;

bool systemLoopback = args.Contains("--system-loopback");
bool listSessions = args.Contains("--list");
bool micDefault = args.Contains("--mic-default");
int activateOnlyIdx = Array.IndexOf(args, "--activate-only");
int secondsIdx = Array.IndexOf(args, "--seconds");
int runSeconds = 0;
if (secondsIdx >= 0 && secondsIdx + 1 < args.Length) int.TryParse(args[secondsIdx + 1], out runSeconds);

// Positional args = app-name overrides, excluding the values consumed by value-taking flags.
var flagValueIndices = new HashSet<int>();
if (activateOnlyIdx >= 0) flagValueIndices.Add(activateOnlyIdx + 1);
if (secondsIdx >= 0) flagValueIndices.Add(secondsIdx + 1);
string[] positional = args.Where((a, i) => !a.StartsWith("--") && !flagValueIndices.Contains(i)).ToArray();

string outDir = Path.Combine(Environment.GetFolderPath(
    Environment.SpecialFolder.UserProfile), "LocalScribe", "spike");
Directory.CreateDirectory(outDir);

// --- Task 9 gate: isolated activation smoke for one PID ---------------------------------
if (activateOnlyIdx >= 0)
{
    if (activateOnlyIdx + 1 >= args.Length || !uint.TryParse(args[activateOnlyIdx + 1], out uint apid))
    {
        Console.WriteLine("Usage: --activate-only <pid>");
        return;
    }
    Console.WriteLine($"Activating per-process loopback for pid {apid}...");
    var probeClock = new StopwatchClock();
    using var probe = new ProcessLoopbackCapture(apid, probeClock);
    probe.Diagnostic += m => Console.WriteLine("[loopback] " + m);
    long probeSamples = 0;
    probe.FrameAvailable += f => probeSamples += f.Samples.Length;
    try
    {
        probe.Start();
        Console.WriteLine($"loopback activated for pid {apid}");
        Console.WriteLine("ActivationInfo: " + probe.ActivationInfo);
        Thread.Sleep(2000);                       // let a few packets flow
        probe.Stop();
        Console.WriteLine($"captured ~{probeSamples / 16000.0:F2}s ({probeSamples} samples) in 2s");
    }
    catch (Exception ex)
    {
        Console.WriteLine("ACTIVATION FAILED: " + ex.GetType().Name + ": " + ex.Message);
    }
    return;
}

// --- Resolve the target render-session PID ----------------------------------------------
// Webex renders call audio in the CiscoCollabHost.exe media process (a different PID from the UI).
// "new Teams" (ms-teams.exe) currently returns silence for per-process loopback (known issue) - it
// is included last and expected to need Plan B. The active render audio session is the production
// source of truth (what IMeetingDetector will key on in later stages).
string[] appNames = positional.Length > 0
    ? positional
    : new[] { "CiscoCollabHost", "Webex", "Zoom", "ms-teams", "msedgewebview2", "Teams" };

// Scan ALL active render endpoints, not just the Multimedia default: communications apps
// (Teams/Webex/Zoom) frequently render call audio to the COMMUNICATIONS device, which may be a
// different physical device than the Multimedia default. Per-process loopback only needs the PID,
// so any endpoint hosting the session is fine.
var enumerator = new MMDeviceEnumerator();

uint renderPid = 0;
string renderImage = "";
var active = new List<(uint pid, string image, string device)>();

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
        string image;
        try { image = pid == 0 ? "(system)" : Process.GetProcessById((int)pid).ProcessName; }
        catch { continue; }                               // process may have just exited

        active.Add((pid, image, device.FriendlyName));
        if (renderPid == 0 && appNames.Any(n => image.Contains(n, StringComparison.OrdinalIgnoreCase)))
        { renderPid = pid; renderImage = image; }
    }
}

if (listSessions)
{
    Console.WriteLine("Active render sessions across all endpoints:");
    if (active.Count == 0) Console.WriteLine("  (none - is audio actually playing right now?)");
    foreach (var (pid, image, dev) in active)
        Console.WriteLine($"  pid {pid,-6} {image}.exe   [{dev}]");

    Console.WriteLine();
    Console.WriteLine("Capture (mic) devices - local.wav uses the Communications default (override: --mic-default):");
    try { Console.WriteLine("  default Communications: " + enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications).FriendlyName); }
    catch { Console.WriteLine("  default Communications: (none)"); }
    try { Console.WriteLine("  default Multimedia:     " + enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).FriendlyName); }
    catch { Console.WriteLine("  default Multimedia:     (none)"); }
    foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        Console.WriteLine("  active mic: " + d.FriendlyName);
    return;
}

if (renderPid == 0 && !systemLoopback)
{
    Console.WriteLine("No active meeting render session found among: " + string.Join(", ", appNames));
    if (active.Count > 0)
    {
        Console.WriteLine("These render sessions ARE active right now:");
        foreach (var (pid, image, dev) in active)
            Console.WriteLine($"  pid {pid,-6} {image}.exe   [{dev}]");
        Console.WriteLine("If your meeting app is above under a different name, pass it explicitly, e.g.:");
        Console.WriteLine("  dotnet run --project src/LocalScribe.SpikeRunner -- " + active[0].image);
    }
    else
    {
        Console.WriteLine("No render sessions are active at all - make sure audio is actually playing when you launch.");
    }
    Console.WriteLine("Or use --system-loopback (Plan B - needed for Teams anyway; see below).");
    return;
}

// Some apps cannot be captured per-process: new Teams (ms-teams.exe) returns all-zeros because it
// registers two render sessions on one PID, and webview/browser hosts share a single audio process.
// For those, auto-fall-back to system-wide loopback so the remote stream is not silently empty.
string[] fullMixApps = { "ms-teams", "Teams", "msedgewebview2" };
bool needsFullMix = renderImage.Length > 0
    && fullMixApps.Any(n => renderImage.Contains(n, StringComparison.OrdinalIgnoreCase));
bool useSystemLoopback = systemLoopback || needsFullMix;

if (needsFullMix && !systemLoopback)
{
    Console.WriteLine($"NOTE: {renderImage}.exe does not support isolated per-process loopback");
    Console.WriteLine("  (Teams registers two render sessions per PID / browsers share one audio process).");
    Console.WriteLine("  Falling back to SYSTEM-WIDE loopback: remote.wav will include ALL system audio");
    Console.WriteLine("  (possible bleed). Use headphones + mute notifications for a clean recording.");
}

Console.WriteLine(renderPid != 0
    ? $"Target render session: pid {renderPid} ({renderImage}.exe)" + (useSystemLoopback ? "  [system-wide loopback]" : "  [per-process]")
    : "[system-wide loopback - no specific meeting render session targeted]");

// --- Dual capture -----------------------------------------------------------------------
// Declare the sinks BEFORE the sources so disposal order (reverse of declaration) is
// loop, mic, remoteSink, localSink: each source's Dispose (which detaches its handler /
// stops capture) runs before its sink, so no late frame can be written to a disposed sink.
using var localSink = new WavSink(Path.Combine(outDir, "local.wav"));
using var remoteSink = new WavSink(Path.Combine(outDir, "remote.wav"));

var clock = new StopwatchClock();
using var mic = new MicCaptureSource(clock, micDefault ? Role.Multimedia : Role.Communications);
Console.WriteLine("Mic: " + mic.DeviceName + (micDefault ? "  [--mic-default: Multimedia]" : "  [Communications default]"));
// Per-process INCLUDE on the render pid (clean isolation) unless full-mix is needed/requested
// (Teams/browser auto-fallback above, or explicit --system-loopback).
using ICaptureSource loop = useSystemLoopback
    ? ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock)
    : new ProcessLoopbackCapture(renderPid, clock);
if (loop is ProcessLoopbackCapture loopDiag)
    loopDiag.Diagnostic += m => Console.WriteLine("[loopback] " + m);

long localSamples = 0, remoteSamples = 0;
float localPeak = 0f, remotePeak = 0f;
mic.FrameAvailable += f => { lock (localSink) { localSink.Write(f.Samples); localSamples += f.Samples.Length; for (int i = 0; i < f.Samples.Length; i++) { float a = Math.Abs(f.Samples[i]); if (a > localPeak) localPeak = a; } } };
loop.FrameAvailable += f => { lock (remoteSink) { remoteSink.Write(f.Samples); remoteSamples += f.Samples.Length; for (int i = 0; i < f.Samples.Length; i++) { float a = Math.Abs(f.Samples[i]); if (a > remotePeak) remotePeak = a; } } };

mic.Start();
try
{
    loop.Start();
}
catch (Exception ex)
{
    Console.WriteLine("LOOPBACK ACTIVATION FAILED: " + ex.GetType().Name + ": " + ex.Message);
    Console.WriteLine("(For Plan B, re-run with: --system-loopback)");
    mic.Stop();
    return;
}
if (loop is ProcessLoopbackCapture plc)
    Console.WriteLine("Loopback " + plc.ActivationInfo);

if (runSeconds > 0)
{
    Console.WriteLine($"Recording both streams headless for {runSeconds}s...");
    Thread.Sleep(runSeconds * 1000);
}
else
{
    Console.WriteLine("Recording both streams. Press ENTER to stop...");
    Console.ReadLine();
}
mic.Stop();
loop.Stop();

// peak > 0 means real captured audio; peak == 0 with samples > 0 means all-silence (Teams zero-bug
// or an AUTOCONVERTPCM path that activated but delivered no real audio).
Console.WriteLine($"local.wav : {localSamples / 16000.0:F1}s ({localSamples} samples) peak {localPeak:F4}");
Console.WriteLine($"remote.wav: {remoteSamples / 16000.0:F1}s ({remoteSamples} samples) peak {remotePeak:F4}");
Console.WriteLine($"Files in: {outDir}");

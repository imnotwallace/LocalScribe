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

using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using LocalScribe.Core.Audio;

string[] positional = args.Where(a => !a.StartsWith("--")).ToArray();
bool systemLoopback = args.Contains("--system-loopback");
int activateOnlyIdx = Array.IndexOf(args, "--activate-only");

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

var render = new MMDeviceEnumerator()
    .GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
var sessions = render.AudioSessionManager.Sessions;

uint renderPid = 0;
string renderImage = "";
for (int i = 0; i < sessions.Count; i++)
{
    var s = sessions[i];
    if (s.State != AudioSessionState.AudioSessionStateActive) continue;
    string image;
    try { image = Process.GetProcessById((int)s.GetProcessID).ProcessName; }
    catch { continue; }                               // session may have just exited
    if (appNames.Any(n => image.Contains(n, StringComparison.OrdinalIgnoreCase)))
    { renderPid = s.GetProcessID; renderImage = image; break; }
}

if (renderPid == 0 && !systemLoopback)
{
    Console.WriteLine("No active meeting render session found among: " + string.Join(", ", appNames));
    Console.WriteLine("Start/join a call so the app is actively playing audio, or use --system-loopback (Plan B).");
    return;
}

Console.WriteLine(renderPid != 0
    ? $"Target render session: pid {renderPid} ({renderImage}.exe)" + (systemLoopback ? "  [Plan B: system loopback]" : "")
    : "[Plan B: system loopback - no specific meeting render session targeted]");

// --- Dual capture -----------------------------------------------------------------------
// Declare the sinks BEFORE the sources so disposal order (reverse of declaration) is
// loop, mic, remoteSink, localSink: each source's Dispose (which detaches its handler /
// stops capture) runs before its sink, so no late frame can be written to a disposed sink.
using var localSink = new WavSink(Path.Combine(outDir, "local.wav"));
using var remoteSink = new WavSink(Path.Combine(outDir, "remote.wav"));

var clock = new StopwatchClock();
using var mic = new MicCaptureSource(clock);
// Default path: per-process INCLUDE on the render pid. Plan B: full-system loopback minus our own pid.
using ICaptureSource loop = systemLoopback
    ? ProcessLoopbackCapture.SystemLoopbackExcludingSelf(clock)
    : new ProcessLoopbackCapture(renderPid, clock);
if (loop is ProcessLoopbackCapture loopDiag)
    loopDiag.Diagnostic += m => Console.WriteLine("[loopback] " + m);

long localSamples = 0, remoteSamples = 0;
mic.FrameAvailable += f => { lock (localSink) { localSink.Write(f.Samples); localSamples += f.Samples.Length; } };
loop.FrameAvailable += f => { lock (remoteSink) { remoteSink.Write(f.Samples); remoteSamples += f.Samples.Length; } };

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

Console.WriteLine("Recording both streams. Press ENTER to stop...");
Console.ReadLine();
mic.Stop();
loop.Stop();

Console.WriteLine($"local.wav : {localSamples / 16000.0:F1}s ({localSamples} samples)");
Console.WriteLine($"remote.wav: {remoteSamples / 16000.0:F1}s ({remoteSamples} samples)");
Console.WriteLine($"Files in: {outDir}");

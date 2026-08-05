using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Session lifecycle and transcription-downgrade logging (Tier 1 plan A, 2026-08-05,
/// spec item T1-1: "session start/stop/recovery, transcription downgrades"). The methods are
/// called directly here and wired to SessionController's events in App.xaml.cs - the
/// StartupOrchestrator/StopConfirmToastGuard shape, because App.xaml.cs has no coverage.</summary>
public sealed class SessionDiagnosticsRecorderTests
{
    private static (SessionDiagnosticsRecorder Rec, FakeDiagnosticLog Log) Make(string? id = "s-1")
    {
        var log = new FakeDiagnosticLog();
        return (new SessionDiagnosticsRecorder(log, () => id), log);
    }

    [Fact]
    public void Every_state_change_is_an_info_line_carrying_the_session_id()
    {
        var (rec, log) = Make();
        rec.StateChanged(SessionState.Recording);
        rec.StateChanged(SessionState.Finalizing);

        Assert.Equal(new[] { "State Recording", "State Finalizing" },
            log.Entries.Select(e => e.Message).ToArray());
        Assert.All(log.Entries, e => Assert.Equal("info", e.Level));
        Assert.All(log.Entries, e => Assert.Equal("session", e.Source));
        // The id is Mark()-wrapped (fix round 1 shape, applied here 2026-08-05): SessionId.cs
        // mints yyyy-MM-dd_HHmm_{App}_{Slug(title)}, i.e. the matter/client name, so
        // Settings.Logging.IncludeTranscriptText - not this test - decides whether the id reaches
        // disk in the clear.
        Assert.All(log.Entries, e => Assert.Equal("session=<<s-1>>", e.Detail));
    }

    [Fact]
    public void The_session_id_is_read_per_call_not_captured()
    {
        // CurrentSessionId is null again by the time Idle arrives, and null before Start - a
        // captured value would mislabel every line either side of a session.
        var (rec, log) = Make(id: null);
        rec.StateChanged(SessionState.Idle);
        Assert.Equal("session=(none)", Assert.Single(log.Entries).Detail);
    }

    [Fact]
    public void Transcription_downgrades_are_warnings_that_name_the_cause()
    {
        var (rec, log) = Make();
        rec.ErrorRaised("VRAM_OOM");
        rec.ErrorRaised("RTF_LAGGING");
        rec.ErrorRaised("TRANSCRIPTION_FAILED");
        rec.ErrorRaised("SOMETHING_NEW");

        Assert.All(log.Entries, e => Assert.Equal("warn", e.Level));
        Assert.Contains("VRAM", log.Entries[0].Message);
        Assert.Contains("lag", log.Entries[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("audio still recording", log.Entries[2].Message);
        // An unknown code must still be recorded verbatim rather than dropped - Plan C adds more.
        Assert.Contains("SOMETHING_NEW", log.Entries[3].Message);
        Assert.All(log.Entries, e => Assert.Contains("code=", e.Detail!));
    }

    [Fact]
    public void Controller_notices_are_logged_as_written()
    {
        // These are FIXED operator strings from SessionController (e.g. the per-process capture
        // fallback at :590). They carry no transcript text, which is why they can be logged whole.
        var (rec, log) = Make();
        rec.Notice("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
        var only = Assert.Single(log.Entries);
        Assert.Equal("info", only.Level);
        Assert.StartsWith("Per-process capture unavailable", only.Message);
    }

    [Fact]
    public void Finalize_completion_names_the_session_it_finished()
    {
        // FinalizeCompleted fires from a background drain AFTER the controller is Idle again, so
        // it takes the id as an ARGUMENT rather than reading the live probe.
        var (rec, log) = Make(id: null);
        rec.FinalizeCompleted("s-42");
        var only = Assert.Single(log.Entries);
        Assert.Equal("Finalize completed", only.Message);
        Assert.Equal("session=<<s-42>>", only.Detail);
    }
}

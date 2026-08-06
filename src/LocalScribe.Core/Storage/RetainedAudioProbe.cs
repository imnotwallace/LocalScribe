// src/LocalScribe.Core/Storage/RetainedAudioProbe.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Existence-based retained-leg probe for the Core side (Tier 1B design 2026-08-05, T1-2).
///
/// DELIBERATELY UNCONDITIONAL on SessionRecord.RetainedAudioSources - that is the entire point.
/// The two existing probes both consult the very list this one has to REBUILD:
/// LocalScribe.App.Services.AudioLegProbe.Resolve returns null when !retained.Contains(kind) before
/// it ever touches the filesystem (and lives in the App assembly, which Core cannot reference), and
/// RetranscriptionRunner.ResolveLegs is private with the identical gate. Reusing either would return
/// nothing for exactly the crashed sessions this exists to repair.
///
/// FLAC first then WAV, both checked for BOTH legs: SessionWriter is constructed with
/// settings.Current (MaintenanceService.RecoverAllAsync), i.e. the format configured NOW, not the
/// format the crashed session actually recorded in - so a preferred-format-only probe would lose a
/// WAV recording on a machine since switched to FLAC. Local first, matching the live pipeline's feed
/// order and RetranscriptionRunner.ResolveLegs.
///
/// Pure and fail-soft: no IO beyond File.Exists, never throws. A session recorded with
/// AudioRetention == "never" legitimately has no legs and correctly probes empty.</summary>
public static class RetainedAudioProbe
{
    public static IReadOnlyList<(SourceKind Kind, string Path)> Legs(StoragePaths paths, string sessionId)
    {
        var legs = new List<(SourceKind, string)>();
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
        {
            try
            {
                string flac = paths.AudioFile(sessionId, kind, AudioFormat.Flac);
                string wav = paths.AudioFile(sessionId, kind, AudioFormat.Wav);
                if (File.Exists(flac)) legs.Add((kind, flac));
                else if (File.Exists(wav)) legs.Add((kind, wav));
            }
            catch
            {
                // Fail-soft (mirrors FlacPcmReader.DurationMs's own contract): a locked or
                // permission-denied leg degrades to "not found", never to an exception. An
                // exception escaping here would land in MaintenanceService.RecoverAllAsync's
                // failures list and strand the session unrecovered on EVERY subsequent launch.
            }
        }
        return legs;
    }
}

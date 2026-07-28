// src/LocalScribe.App/Services/AudioLegProbe.cs
using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>Resolves a session's retained audio leg on disk. Extracted from
/// SplitSpeakersViewModel.ProbeLeg (design 2026-07-28 task 5) so the import-time detection step
/// points the diariser at EXACTLY the same file the manual dialog does - there is no Origin branch
/// anywhere, imported and recorded sessions resolve identically. Mirrors PlaybackViewModel.Resolve:
/// retained-list check, then the preferred on-disk format, then the other, so a session recorded
/// before a format change still resolves.</summary>
public static class AudioLegProbe
{
    public static string? Resolve(StoragePaths paths, string sessionId, SourceKind kind,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
    {
        if (!retained.Contains(kind)) return null;
        string preferred = paths.AudioFile(sessionId, kind, preferredFormat);
        if (File.Exists(preferred)) return preferred;
        var other = preferredFormat == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
        string alternate = paths.AudioFile(sessionId, kind, other);
        return File.Exists(alternate) ? alternate : null;
    }
}

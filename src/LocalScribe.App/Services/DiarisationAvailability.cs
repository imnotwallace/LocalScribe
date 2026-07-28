// src/LocalScribe.App/Services/DiarisationAvailability.cs
using System.IO;
using LocalScribe.Core.Diarisation;

namespace LocalScribe.App.Services;

/// <summary>Pre-flight gate for speaker detection (design 2026-07-28 section 5), mirroring the
/// import model-presence gate at AudioImporter.cs:77-92: refuse visibly and up front rather than
/// crash after minutes of transcription.
///
/// This has to probe for itself. ModelPaths.Resolve is a bare Path.Combine with no existence check
/// (ModelPaths.cs:23, deliberate), ModelPaths.AvailableModels only enumerates ggml-*.bin so sherpa
/// models are invisible to it, and LocalScribe.Diarizer.exe is deployed by no build step at all
/// (App.csproj:32-38 - a same-folder copy would overwrite App's onnxruntime.dll 1.22 with sherpa's
/// 1.24.4 and is "actively unsafe"). A missing exe does NOT surface as DiarisationException:
/// Process.Start throws Win32Exception out of ProcessDiarisationHelper.cs:33 and
/// SherpaHelperDiariser.cs:47 does not catch it.</summary>
public static class DiarisationAvailability
{
    /// <summary>Returns a user-facing reason speaker detection is unavailable, or null when the
    /// helper exe and both sherpa models are present and non-empty.</summary>
    public static string? Probe(Func<string, string> resolveModel, string exePath)
    {
        if (!Present(exePath))
            return "Speaker detection unavailable - LocalScribe.Diarizer.exe is not installed.";
        if (!Present(resolveModel(DiarisationModels.Segmentation)))
            return "Speaker detection unavailable - the speaker segmentation model is not installed.";
        if (!Present(resolveModel(DiarisationModels.Embedding)))
            return "Speaker detection unavailable - the speaker embedding model is not installed.";
        return null;
    }

    // Zero-byte counts as absent: a truncated download is not a usable model, and the publish
    // guards (tools/verify-*.ps1) use the same missing-or-empty test.
    private static bool Present(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // unreadable is unavailable; never throw out of a pre-flight probe
        }
    }
}

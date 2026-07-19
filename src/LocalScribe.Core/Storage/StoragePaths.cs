using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Resolves the storage root and the section 9 session/matter folder layout. All getters are pure.</summary>
public sealed class StoragePaths
{
    public string Root { get; }
    public StoragePaths(string configuredRoot)
        => Root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredRoot));

    public string SessionsDir => Path.Combine(Root, "sessions");
    public string MattersDir => Path.Combine(Root, "matters");
    public string SessionDir(string id) => Path.Combine(SessionsDir, id);

    public string SessionJson(string id) => Path.Combine(SessionDir(id), "session.json");
    public string MetaJson(string id) => Path.Combine(SessionDir(id), "meta.json");
    public string TranscriptJsonl(string id) => Path.Combine(SessionDir(id), "transcript.jsonl");
    public string EditsJson(string id) => Path.Combine(SessionDir(id), "edits.json");
    public string SpeakersJson(string id) => Path.Combine(SessionDir(id), "speakers.json");
    public string TranscriptMd(string id) => Path.Combine(SessionDir(id), "transcript.md");
    public string TranscriptTxt(string id) => Path.Combine(SessionDir(id), "transcript.txt");
    public string SessionTxt(string id) => Path.Combine(SessionDir(id), "session.txt");
    public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");

    /// <summary>Assistant work-product sidecars (design 2026-07-18 section 7.3): DERIVED
    /// artifacts stored separately from the transcript, never touching transcript files.
    /// Rides into zip archives automatically (SessionArchiver walks AllDirectories).</summary>
    public string AssistantDir(string id) => Path.Combine(SessionDir(id), "assistant");
    public string SummariesJson(string id) => Path.Combine(AssistantDir(id), "summaries.json");

    /// <summary>Imported-session provenance folder (design 2026-07-13 section 4.1): the original
    /// file is archived byte-for-byte as source\{original-filename}. Absent for recorded sessions.</summary>
    public string SourceDir(string id) => Path.Combine(SessionDir(id), "source");
    public string SourceFile(string id, string originalFileName)
        => Path.Combine(SourceDir(id), originalFileName);

    // Versioned re-transcription (design 2026-07-13 section 3.1). "v1" resolves to the session
    // root, so every version-aware overload below degenerates to the pre-versioning layout for
    // un-versioned sessions - callers can always go through these.
    public string VersionsDir(string id) => Path.Combine(SessionDir(id), "versions");
    public string VersionDir(string id, string versionId)
        => versionId == TranscriptVersions.Root ? SessionDir(id) : Path.Combine(VersionsDir(id), versionId);
    public string TranscriptJsonl(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "transcript.jsonl");
    public string EditsJson(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "edits.json");
    public string SpeakersJson(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "speakers.json");
    public string TranscriptMd(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "transcript.md");
    public string TranscriptTxt(string id, string versionId) => Path.Combine(VersionDir(id, versionId), "transcript.txt");

    public string AudioFile(string id, SourceKind source, AudioFormat format)
    {
        string stem = source == SourceKind.Local ? "local" : "remote";
        string ext = format == AudioFormat.Flac ? "flac" : "wav";
        return Path.Combine(SessionDir(id), $"{stem}.{ext}");
    }

    public string MattersIndexJson => Path.Combine(MattersDir, "matters.json");
    public string MatterJson(string matterId) => Path.Combine(MattersDir, matterId, "matter.json");

    /// <summary>Persisted cross-session search cache (design 2026-07-13 section 2.1): DERIVED,
    /// self-healing, safe to delete - never evidence. Lives under its own index\ folder beside
    /// sessions\ and matters\.</summary>
    public string SearchIndexJson => Path.Combine(Root, "index", "search-index.json");
}

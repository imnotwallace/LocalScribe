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

    public string AudioFile(string id, SourceKind source, AudioFormat format)
    {
        string stem = source == SourceKind.Local ? "local" : "remote";
        string ext = format == AudioFormat.Flac ? "flac" : "wav";
        return Path.Combine(SessionDir(id), $"{stem}.{ext}");
    }

    public string MattersIndexJson => Path.Combine(MattersDir, "matters.json");
    public string MatterJson(string matterId) => Path.Combine(MattersDir, matterId, "matter.json");
}

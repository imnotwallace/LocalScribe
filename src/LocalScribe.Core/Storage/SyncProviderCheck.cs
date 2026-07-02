namespace LocalScribe.Core.Storage;

/// <summary>Warns when the storage root resolves under a known sync provider (spec section 7/Storage
/// format) - pushing audio/transcripts off-machine fights the local-only goal.</summary>
public static class SyncProviderCheck
{
    private static readonly string[] Known = { "OneDrive", "Dropbox", "Google Drive", "GoogleDrive" };

    public static bool ResolvesUnderSyncProvider(string expandedPath, out string? provider)
        => ResolvesUnderSyncProvider(expandedPath, Known, out provider);

    public static bool ResolvesUnderSyncProvider(string expandedPath, IReadOnlyList<string> knownNames, out string? provider)
    {
        string[] segments = expandedPath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in knownNames)
        {
            bool hit = segments.Any(seg =>
                seg.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                seg.StartsWith(name + " -", StringComparison.OrdinalIgnoreCase));   // "OneDrive - Contoso"
            if (hit) { provider = name; return true; }
        }
        provider = null;
        return false;
    }
}

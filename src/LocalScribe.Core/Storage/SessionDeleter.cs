// src/LocalScribe.Core/Storage/SessionDeleter.cs
namespace LocalScribe.Core.Storage;

/// <summary>Whole-session delete: the session FOLDER goes to the Recycle Bin, never a permanent
/// unlink (design 3.4). This is the ONLY deletion of session/transcript data in the product
/// (evidentiary invariant, spec 1.1/1.6). Policy lives in the caller (MaintenanceService):
/// close open read views first (audio file handles), refuse live/pending-recovery sessions,
/// then ApplyTagDeltaAsync for the tagged matters. This class is mechanism only.</summary>
public sealed class SessionDeleter(StoragePaths paths, IRecycleBin bin)
{
    public Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string dir = paths.SessionDir(sessionId);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Session folder not found: {dir}");
        bin.SendToRecycleBin(dir);
        return Task.CompletedTask;
    }
}

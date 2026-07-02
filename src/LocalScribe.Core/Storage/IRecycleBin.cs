// src/LocalScribe.Core/Storage/IRecycleBin.cs
namespace LocalScribe.Core.Storage;

/// <summary>Seam over the OS recycle operation so Core stays shell-free and the deleters are
/// unit-testable with a recording fake. The real implementation (SHFileOperationW) lives in the
/// App project (Services/ShellRecycleBin.cs) and is exercised only by the smoke runbook.</summary>
public interface IRecycleBin
{
    /// <summary>Sends the file or directory at <paramref name="path"/> to the Windows Recycle
    /// Bin (recoverable - design 3.4). Throws on failure; never permanently unlinks.</summary>
    void SendToRecycleBin(string path);
}

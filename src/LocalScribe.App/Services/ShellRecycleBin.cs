// src/LocalScribe.App/Services/ShellRecycleBin.cs
using System.IO;
using System.Runtime.InteropServices;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>The real IRecycleBin: sends a file or folder to the Windows Recycle Bin via
/// SHFileOperationW with FOF_ALLOWUNDO (recoverable - design 3.4, never a permanent unlink).
/// Shell side effect: NO unit test by design; verified by the Stage 4 smoke runbook's
/// delete-to-recycle-bin step. Lives in the App project so Core stays shell-free.</summary>
public sealed class ShellRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;        // recycle, do not unlink
    private const ushort FOF_NOCONFIRMATION = 0x0010;   // our own dialog already confirmed
    private const ushort FOF_SILENT = 0x0004;           // no shell progress UI

    public void SendToRecycleBin(string path)
    {
        // SHFileOperationW requires an absolute path; relative paths are resolved against an
        // unspecified directory. StoragePaths roots are already full paths; normalize anyway.
        string full = Path.GetFullPath(path);
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            // pFrom is a double-null-terminated list of paths. The string marshaller appends
            // one terminating null; the explicit "\0" below supplies the second.
            pFrom = full + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
        };
        int result = SHFileOperationW(ref op);
        if (result != 0)
            throw new IOException(
                $"Recycle of '{full}' failed (SHFileOperationW returned 0x{result:X})");
        if (op.fAnyOperationsAborted != 0)
            throw new IOException($"Recycle of '{full}' was aborted by the shell");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    // Sequential layout matches the 64-bit SHFILEOPSTRUCTW (the struct is only packed on
    // 32-bit Windows headers; this app ships x64-only, same as the whisper native deps).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;   // Win32 BOOL
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
}

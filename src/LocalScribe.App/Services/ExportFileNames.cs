using System.IO;
namespace LocalScribe.App.Services;

/// <summary>Default export file names: replace characters Windows forbids in a file name with '_'
/// so the Save-As dialog gets a usable default (Stage 6.3). Shared by the session export dialog and
/// the matter archive export - legal matter references commonly contain '/' (e.g. "2026/014").</summary>
public static class ExportFileNames
{
    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string s = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(s) ? "export" : s;
    }
}

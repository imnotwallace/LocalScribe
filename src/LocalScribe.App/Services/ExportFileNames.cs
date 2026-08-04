using System.IO;
using System.Text;
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

    /// <summary>Expand a Save-As filename template (design 2026-08-04 section 6). Three rules:
    /// an UNKNOWN token is left literal, so the user sees their typo in the Save-As default name
    /// and fixes it (silently dropping it hides the mistake); an EMPTY token swallows the
    /// separator run that followed it, so "{matter}-{title}" on an untagged session is "Title",
    /// not "-Title"; separators between non-empty tokens are untouched. Call Sanitize on the
    /// result - this method deliberately does not, so the two concerns stay testable apart.</summary>
    public static string Expand(string template, IReadOnlyDictionary<string, string> tokens)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    string name = template[(i + 1)..close];
                    if (tokens.TryGetValue(name, out string? value))
                    {
                        i = close + 1;
                        if (value.Length == 0)
                        {
                            while (i < template.Length && template[i] is ' ' or '-' or '_') i++;
                            continue;
                        }
                        sb.Append(value);
                        continue;
                    }
                    sb.Append(template, i, close - i + 1);   // unknown token: literal
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(template[i]);
            i++;
        }
        return sb.ToString().Trim(' ', '_', '-');
    }
}

using System.IO;
using System.Text.Json;
namespace LocalScribe.App.ViewModels;

/// <summary>Volatile overlay position (spec 7: throwaway window-state.json, NOT settings).
/// Any load failure is null - this file is never truth, never worth an error.</summary>
public sealed class WindowStateStore(string path)
{
    private sealed record State(double X, double Y);

    public (double X, double Y)? Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return s is null ? null : (s.X, s.Y);
        }
        catch { return null; }
    }

    public void Save(double x, double y)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new State(x, y)));
        }
        catch { /* volatile state - losing it costs one re-drag */ }
    }
}

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LocalScribe.App.ViewModels;

/// <summary>Remembered window geometry: X/Y always, Width/Height only for resizable windows
/// (the overlay pill saves position only).</summary>
public sealed record WindowPlacement(double X, double Y, double? Width = null, double? Height = null);

/// <summary>Volatile per-window placement (spec 7: throwaway window-state.json, NOT settings,
/// deliberately no schemaVersion - design section 8 exemption). Keyed map
/// {"windows":{"overlay":{"x":..,"y":..},"main":{"x":..,"y":..,"width":..,"height":..}}};
/// a legacy pre-Stage-4 bare {x,y} root shape-detects on read as the "overlay" entry.
/// Any failure is null/ignored - this file is never truth, never worth an error.</summary>
public sealed class WindowStateStore(string path)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Placement(double X, double Y, double? Width = null, double? Height = null);

    // One shape reads both formats: keyed files bind Windows, legacy files bind X/Y.
    private sealed record FileShape(
        Dictionary<string, Placement>? Windows = null, double? X = null, double? Y = null);

    public WindowPlacement? Load(string key)
    {
        var map = ReadMap();
        return map is not null && map.TryGetValue(key, out var p)
            ? new WindowPlacement(p.X, p.Y, p.Width, p.Height) : null;
    }

    public void Save(string key, WindowPlacement placement)
    {
        try
        {
            // Read-modify-write so saving one window's placement never drops another's
            // (and folds a legacy bare {x,y} file into the keyed map as "overlay").
            var map = ReadMap() ?? new Dictionary<string, Placement>(StringComparer.Ordinal);
            map[key] = new Placement(placement.X, placement.Y, placement.Width, placement.Height);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new FileShape(map), JsonOpts));
        }
        catch { /* volatile state - losing it costs one re-drag */ }
    }

    private Dictionary<string, Placement>? ReadMap()
    {
        try
        {
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), JsonOpts);
            if (shape?.Windows is { } keyed)
                return new Dictionary<string, Placement>(keyed, StringComparer.Ordinal);
            if (shape is { X: { } lx, Y: { } ly })     // legacy bare {x,y}: the overlay's position
                return new Dictionary<string, Placement>(StringComparer.Ordinal)
                { ["overlay"] = new Placement(lx, ly) };
            return null;
        }
        catch { return null; }
    }
}

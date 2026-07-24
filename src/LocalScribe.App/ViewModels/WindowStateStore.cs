using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace LocalScribe.App.ViewModels;

/// <summary>Remembered window geometry: X/Y always, Width/Height only for resizable windows
/// (the overlay pill saves position only).</summary>
public sealed record WindowPlacement(double X, double Y, double? Width = null, double? Height = null);

/// <summary>Remembered assistant-panel state per window FAMILY (addendum 2026-07-25): one bit +
/// width for all read views, one for the matters page - NOT per session/matter (the placement
/// store's own single-key-per-family scheme). Presence of an entry means the user made an
/// EXPLICIT choice; absence means the open-iff-history heuristic applies.</summary>
public sealed record AssistantPanelState(bool Open, double Width);

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

    private sealed record PanelState(bool Open, double Width);

    // One shape reads both formats: keyed files bind Windows, legacy files bind X/Y.
    private sealed record FileShape(
        Dictionary<string, Placement>? Windows = null, double? X = null, double? Y = null,
        string? LastExportDir = null,
        Dictionary<string, PanelState>? AssistantPanel = null);

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
            var shape = ReadShape();
            Write(new FileShape(map, LastExportDir: shape?.LastExportDir,
                AssistantPanel: shape?.AssistantPanel));
        }
        catch { /* volatile state - losing it costs one re-drag */ }
    }

    public string? LoadLastExportDir()
    {
        var dir = ReadShape()?.LastExportDir;
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }

    public void SaveLastExportDir(string dir)
    {
        try
        {
            var shape = ReadShape();
            Write(new FileShape(ReadMap(), LastExportDir: dir, AssistantPanel: shape?.AssistantPanel));
        }
        catch { /* volatile state - losing it costs one re-pick */ }
    }

    public AssistantPanelState? LoadAssistantPanel(string key)
    {
        var panels = ReadShape()?.AssistantPanel;
        return panels is not null && panels.TryGetValue(key, out var p)
            ? new AssistantPanelState(p.Open, p.Width) : null;
    }

    public void SaveAssistantPanel(string key, AssistantPanelState state)
    {
        try
        {
            var shape = ReadShape();
            var panels = shape?.AssistantPanel is { } existing
                ? new Dictionary<string, PanelState>(existing, StringComparer.Ordinal)
                : new Dictionary<string, PanelState>(StringComparer.Ordinal);
            panels[key] = new PanelState(state.Open, state.Width);
            Write(new FileShape(ReadMap(), LastExportDir: shape?.LastExportDir,
                AssistantPanel: panels));
        }
        catch { /* volatile state - losing it costs one re-toggle */ }
    }

    private void Write(FileShape shape)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(shape, JsonOpts));
    }

    private FileShape? ReadShape()
    {
        try { return JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), JsonOpts); }
        catch { return null; }
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

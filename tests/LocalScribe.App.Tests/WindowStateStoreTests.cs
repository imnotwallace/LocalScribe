using System.IO;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class WindowStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "ls-ws-" + Guid.NewGuid().ToString("N"), "window-state.json");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch { } }

    [Fact]
    public void Keyed_roundtrip_with_size()
    {
        new WindowStateStore(_path).Save("main", new WindowPlacement(10.5, 20.25, 1080, 720));
        Assert.Equal(new WindowPlacement(10.5, 20.25, 1080, 720),
            new WindowStateStore(_path).Load("main"));
    }

    [Fact]
    public void Keyed_roundtrip_position_only()
    {
        new WindowStateStore(_path).Save("overlay", new WindowPlacement(123.5, 67.25));
        Assert.Equal(new WindowPlacement(123.5, 67.25),
            new WindowStateStore(_path).Load("overlay"));
    }

    [Fact]
    public void Save_preserves_other_keys()
    {
        var store = new WindowStateStore(_path);
        store.Save("overlay", new WindowPlacement(1, 2));
        store.Save("main", new WindowPlacement(3, 4, 800, 600));
        Assert.Equal(new WindowPlacement(1, 2), store.Load("overlay"));
        Assert.Equal(new WindowPlacement(3, 4, 800, 600), store.Load("main"));
    }

    [Fact]
    public void Legacy_bare_xy_shape_detects_as_overlay()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Exact output of the pre-Stage-4 writer: Serialize(new State(x, y)) with default options.
        File.WriteAllText(_path, "{\"X\":123.5,\"Y\":67.25}");
        var store = new WindowStateStore(_path);
        Assert.Equal(new WindowPlacement(123.5, 67.25), store.Load("overlay"));
        Assert.Null(store.Load("main"));                   // legacy file only knows the overlay
    }

    [Fact]
    public void Save_over_legacy_file_keeps_the_migrated_overlay_entry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{\"X\":123.5,\"Y\":67.25}");
        var store = new WindowStateStore(_path);
        store.Save("main", new WindowPlacement(3, 4, 800, 600));   // read-modify-write folds legacy in
        Assert.Equal(new WindowPlacement(123.5, 67.25), store.Load("overlay"));
        Assert.Equal(new WindowPlacement(3, 4, 800, 600), store.Load("main"));
    }

    [Fact]
    public void Absent_corrupt_or_unknown_key_returns_null()
    {
        var store = new WindowStateStore(_path);
        Assert.Null(store.Load("overlay"));                // absent file
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{not json");
        Assert.Null(store.Load("overlay"));                // corrupt: throwaway file never throws
        File.WriteAllText(_path, "{\"windows\":{\"overlay\":{\"x\":1,\"y\":2}}}");
        Assert.Null(store.Load("main"));                   // unknown key
        File.WriteAllText(_path, "{}");
        Assert.Null(store.Load("overlay"));                // neither keyed nor legacy shape
    }

    [Fact]
    public void LastExportDir_roundtrips_and_coexists_with_window_placements()
    {
        string path = Path.Combine(Path.GetTempPath(), "ls-ws-" + Guid.NewGuid().ToString("N"), "window-state.json");
        try
        {
            var store = new WindowStateStore(path);
            Assert.Null(store.LoadLastExportDir());

            store.SaveLastExportDir(@"C:\Exports");
            Assert.Equal(@"C:\Exports", store.LoadLastExportDir());

            // A window-placement save must NOT drop the remembered export dir...
            store.Save("main", new WindowPlacement(10, 20, 800, 600));
            Assert.Equal(@"C:\Exports", store.LoadLastExportDir());
            Assert.Equal(10, store.Load("main")!.X);

            // ...and updating the export dir must NOT drop window placements.
            store.SaveLastExportDir(@"C:\Other");
            Assert.Equal(20, store.Load("main")!.Y);
            Assert.Equal(@"C:\Other", store.LoadLastExportDir());
        }
        finally { string? d = Path.GetDirectoryName(path); if (d is not null && Directory.Exists(d)) Directory.Delete(d, true); }
    }
}

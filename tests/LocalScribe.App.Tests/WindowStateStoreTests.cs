using System.IO;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class WindowStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ls-ws-" + Guid.NewGuid().ToString("N"), "window-state.json");
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(_path)!, true); } catch { } }

    [Fact]
    public void Roundtrips_position()
    {
        var store = new WindowStateStore(_path);
        store.Save(123.5, 67.25);
        Assert.Equal((123.5, 67.25), new WindowStateStore(_path).Load());
    }

    [Fact]
    public void Absent_or_corrupt_returns_null()
    {
        Assert.Null(new WindowStateStore(_path).Load());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{not json");
        Assert.Null(new WindowStateStore(_path).Load());   // throwaway file: never throws
    }
}

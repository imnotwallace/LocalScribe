using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ls-settings-" + Guid.NewGuid().ToString("N"));
    public SettingsServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public async Task SaveAsync_persists_swaps_current_and_raises_changed_with_old_and_new()
    {
        string path = Path.Combine(_dir, "settings.json");
        var initial = new Settings();
        var svc = new SettingsService(path, initial);
        Assert.Same(initial, svc.Current);

        Settings? oldSeen = null, newSeen = null;
        svc.Changed += (o, n) => { oldSeen = o; newSeen = n; };

        var updated = initial with { AudioRetention = "never", Timestamps = "wallclock" };
        await svc.SaveAsync(updated, CancellationToken.None);

        Assert.Equal("never", svc.Current.AudioRetention);   // Current swapped
        Assert.Same(initial, oldSeen);                       // Changed carries the OLD snapshot...
        Assert.Same(svc.Current, newSeen);                   // ...and the NEW one (post-swap)
        Assert.Equal("wallclock", newSeen!.Timestamps);

        // Persisted atomically via SettingsStore: a fresh reader sees the saved values.
        var reloaded = await new SettingsStore(path).LoadOrDefaultAsync(CancellationToken.None);
        Assert.Equal("never", reloaded.AudioRetention);
        Assert.Equal("wallclock", reloaded.Timestamps);
    }

    [Fact]
    public async Task SaveAsync_stamps_the_current_schema_version_on_the_held_snapshot()
    {
        string path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path, new Settings());
        await svc.SaveAsync(new Settings { Language = "en" }, CancellationToken.None);
        // Current must equal what a reload sees - including SchemaVersion (SettingsStore stamps
        // the file; the service stamps the in-memory snapshot to match).
        Assert.Equal(SettingsStore.Version, svc.Current.SchemaVersion);
        Assert.Equal("en", svc.Current.Language);
    }
}

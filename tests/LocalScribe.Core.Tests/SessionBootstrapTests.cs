using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-boot-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly DateTimeOffset Now = new(2026, 7, 2, 6, 32, 5, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_creates_folder_meta_and_live_record()
    {
        var paths = new StoragePaths(_root);
        var settings = new Settings { Self = new SelfIdentity { Name = "Sam", Role = "Attorney" } };
        var devices = new DeviceSnapshot
        {
            Mic = new MicSnapshot { Mode = MicMode.FollowDefault, Name = "Shure MV7" },
            Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost", FellBackToSystemMix = false },
        };
        var time = new ManualUtcTimeProvider(Now);

        var info = await SessionBootstrap.StartAsync(paths, settings, AppKind.Webex,
            [SourceKind.Local, SourceKind.Remote], devices, time, "0.3.0", CancellationToken.None);

        Assert.True(Directory.Exists(paths.SessionDir(info.Id)));
        Assert.StartsWith("2026-07-02_", info.Id);                 // local-wall-clock id (spec 9)
        Assert.Contains("Webex", info.Id);

        var meta = await new MetadataStore(paths.MetaJson(info.Id)).LoadAsync(CancellationToken.None);
        Assert.NotNull(meta);
        Assert.Contains(meta!.Participants, p => p.IsSelf && p.Name == "Sam" && p.Side == SourceKind.Local);

        var live = await new SessionStore(paths.SessionJson(info.Id)).ReadAsync(CancellationToken.None);
        Assert.NotNull(live);
        Assert.Null(live!.EndedAtUtc);                             // recovery-compatible live record
        Assert.Equal(AppKind.Webex, live.App);
        Assert.Equal([SourceKind.Local, SourceKind.Remote], live.Sources);
        Assert.Equal("Shure MV7", live.Devices.Mic.Name);
        Assert.Equal(RemoteMode.PerProcess, live.Devices.Remote.Mode);
        Assert.Equal("0.3.0", live.AppVersion);
    }

    [Fact]
    public async Task StartAsync_collision_gets_numeric_suffix()
    {
        var paths = new StoragePaths(_root);
        var settings = new Settings();
        var time = new ManualUtcTimeProvider(Now);

        var a = await SessionBootstrap.StartAsync(paths, settings, AppKind.Manual,
            [SourceKind.Local], new DeviceSnapshot(), time, "0.3.0", CancellationToken.None);
        var b = await SessionBootstrap.StartAsync(paths, settings, AppKind.Manual,
            [SourceKind.Local], new DeviceSnapshot(), time, "0.3.0", CancellationToken.None);

        Assert.NotEqual(a.Id, b.Id);
        Assert.StartsWith(a.Id, b.Id);                             // "...-2" suffix (spec 9)
    }

    [Fact]
    public async Task StartAsync_seeds_meta_matterIds_when_supplied()
    {
        var paths = new StoragePaths(_root);
        var time = new ManualUtcTimeProvider(Now);
        var boot = await SessionBootstrap.StartAsync(paths, new Settings(), AppKind.Webex,
            [SourceKind.Local, SourceKind.Remote], new DeviceSnapshot(), time, "0.3.0",
            default, new[] { "M-2026-014", "M-2026-020" });

        Assert.Equal(new[] { "M-2026-014", "M-2026-020" }, boot.Meta.MatterIds);
        var onDisk = await new MetadataStore(paths.MetaJson(boot.Id)).LoadAsync(default);
        Assert.Equal(new[] { "M-2026-014", "M-2026-020" }, onDisk!.MatterIds);
    }

    [Fact]
    public async Task StartAsync_without_matterIds_leaves_meta_untagged()
    {
        var paths = new StoragePaths(_root);
        var time = new ManualUtcTimeProvider(Now);
        var boot = await SessionBootstrap.StartAsync(paths, new Settings(), AppKind.Webex,
            [SourceKind.Local, SourceKind.Remote], new DeviceSnapshot(), time, "0.3.0", default);

        Assert.Empty(boot.Meta.MatterIds);
    }

    [Fact]
    public async Task StartAsync_honors_a_caller_supplied_title_in_meta_and_id_slug()
    {
        var paths = new StoragePaths(_root);
        var info = await SessionBootstrap.StartAsync(paths, new Settings(), AppKind.Manual,
            [SourceKind.Local], new DeviceSnapshot(), new ManualUtcTimeProvider(Now), "0.3.0",
            CancellationToken.None, matterIds: ["M-2026-001"], title: "Client call re: settlement");

        Assert.Equal("Client call re: settlement", info.Meta.Title);
        Assert.EndsWith("_Manual_client-call-re-settlement", info.Id);   // slug follows the title
        Assert.Equal(["M-2026-001"], info.Meta.MatterIds);
        var meta = await new MetadataStore(paths.MetaJson(info.Id)).LoadAsync(CancellationToken.None);
        Assert.Equal("Client call re: settlement", meta!.Title);
    }
}

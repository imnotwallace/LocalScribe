using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Tests;

/// <summary>Synchronous ISettingsService: SaveAsync swaps Current and raises Changed inline,
/// so VM commits are deterministic in tests (no SpinWait needed on Current).</summary>
public sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(Settings? initial = null) => Current = initial ?? new Settings();
    public Settings Current { get; private set; }
    public int SaveCount { get; private set; }
    public event Action<Settings, Settings>? Changed;

    public Task SaveAsync(Settings updated, CancellationToken ct)
    {
        var old = Current;
        Current = updated;
        SaveCount++;
        Changed?.Invoke(old, updated);
        return Task.CompletedTask;
    }
}

public sealed class FakeUiErrorReporter : IUiErrorReporter
{
    public readonly List<(string Context, Exception Ex)> Reports = new();
    public readonly List<string> Infos = new();
    public void Report(string context, Exception ex) => Reports.Add((context, ex));
    public void Info(string message) => Infos.Add(message);
}

public sealed class FakeRecycleBin : IRecycleBin
{
    public readonly List<string> Recycled = new();
    public void SendToRecycleBin(string path) => Recycled.Add(path);
}

public sealed class FakeLaunchAtLogin : ILaunchAtLogin
{
    public bool Enabled = true;
    public readonly List<bool> SetCalls = new();
    public bool IsEnabled() => Enabled;
    public void SetEnabled(bool on) { Enabled = on; SetCalls.Add(on); }
}

using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.Services;

/// <summary>The app's single mutable Settings holder (design 6.2, first mutation path).
/// SaveAsync persists via SettingsStore (atomic write), swaps Current, then raises
/// Changed(old, new). Thread-safety is reference-swap + event ONLY: Settings is an immutable
/// record, so any reader of Current always sees one coherent snapshot; there is no lock and no
/// torn state by construction. Consumers that must react subscribe to Changed or re-read
/// Current at their next natural decision point (SessionController does so at StartAsync).</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly SettingsStore _store;
    private volatile Settings _current;

    public SettingsService(string settingsJsonPath, Settings initial)
        => (_store, _current) = (new SettingsStore(settingsJsonPath), initial);

    public Settings Current => _current;
    public event Action<Settings, Settings>? Changed;

    public async Task SaveAsync(Settings updated, CancellationToken ct)
    {
        // Stamp the version the store writes, so the in-memory snapshot equals a reload.
        var stamped = updated with { SchemaVersion = SettingsStore.Version };
        await _store.SaveAsync(stamped, ct);
        var old = _current;
        _current = stamped;              // swap only after the disk write succeeded
        Changed?.Invoke(old, stamped);
    }
}

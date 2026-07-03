using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.Services;

/// <summary>The app's single mutable Settings holder (design 6.2, first mutation path).
/// SaveAsync persists via SettingsStore (atomic write), swaps Current, then raises
/// Changed(old, new). Readers of Current always see one coherent immutable snapshot (Settings is
/// a record; the field is a plain reference swap). SaveAsync is SERIALIZED by a SemaphoreSlim
/// across the store-write + swap + Changed, so two overlapping saves cannot collide on
/// settings.json.tmp (AtomicFile's fixed temp path) nor interleave their swaps. Consumers that
/// must react subscribe to Changed or re-read Current at their next decision point.</summary>
public sealed class SettingsService : ISettingsService
{
    private readonly SettingsStore _store;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private volatile Settings _current;

    public SettingsService(string settingsJsonPath, Settings initial)
        => (_store, _current) = (new SettingsStore(settingsJsonPath), initial);

    public Settings Current => _current;
    public event Action<Settings, Settings>? Changed;

    public async Task SaveAsync(Settings updated, CancellationToken ct)
    {
        // Stamp the version the store writes, so the in-memory snapshot equals a reload.
        var stamped = updated with { SchemaVersion = SettingsStore.Version };
        await _saveGate.WaitAsync(ct);
        try
        {
            await _store.SaveAsync(stamped, ct);
            var old = _current;
            _current = stamped;          // swap only after the disk write succeeded
            Changed?.Invoke(old, stamped);
        }
        finally { _saveGate.Release(); }
    }
}

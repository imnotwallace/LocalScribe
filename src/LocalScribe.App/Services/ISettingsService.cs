using LocalScribe.Core.Model;
namespace LocalScribe.App.Services;

/// <summary>The app's single mutable Settings seam (design 6.2). Current is always a coherent
/// immutable snapshot; SaveAsync persists atomically via SettingsStore, swaps Current, then
/// raises Changed(old, new). Implemented by SettingsService (Task 10); MaintenanceService and
/// the Stage 4 ViewModels consume only this interface. WPF-free by house rule.</summary>
public interface ISettingsService
{
    Settings Current { get; }
    event Action<Settings, Settings>? Changed;
    Task SaveAsync(Settings updated, CancellationToken ct);
}

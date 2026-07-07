using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

/// <summary>WPF-free wrapper around an immutable Core <see cref="DisplayRow"/> (design 4.1
/// smoke-fix): adds a per-row IsNowPlaying flag the read view's ItemContainerStyle tints,
/// decoupled from ListView.SelectedIndex so the moving "now playing" highlight can no longer
/// silently overwrite the user's own selection or fire a UIA selection announcement on every
/// section advance. DisplayRow itself must stay untouched (it's the canonical projection
/// output shared with the file renderers).</summary>
public sealed partial class ReadRow : ObservableObject
{
    public DisplayRow Data { get; }
    [ObservableProperty] private bool _isNowPlaying;

    /// <summary>Stage 6.1 XAML conveniences: context-menu enabling reads these (a marker row has
    /// no segments; "Remove speaker pin" needs at least one pinned line). Get-only is fine -
    /// rows are replaced wholesale on every (re)load, never mutated in place.</summary>
    public bool HasSegments => Data.Segments.Count > 0;
    public bool HasPin => Data.HasPin;

    public ReadRow(DisplayRow data) => Data = data;
}

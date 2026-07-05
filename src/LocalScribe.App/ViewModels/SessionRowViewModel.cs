using System.Globalization;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.ViewModels;

/// <summary>One rendered matter tag on a session row (Stage 5.3 Task 4). Text is the short chip
/// label; Tooltip carries the raw matter id so a lingering/deleted-matter tag is still
/// identifiable even once its name/reference no longer resolve.</summary>
public sealed record MatterChip(string Text, string Tooltip);

/// <summary>Immutable presentation row over one catalog entry (design 3.2). All display strings
/// are computed once at construction; a refresh rebuilds rows rather than mutating them.</summary>
public sealed class SessionRowViewModel
{
    public string Id { get; }
    public string Title { get; }
    public string AppMedium { get; }
    public string DateDisplay { get; }
    public string DateTooltip { get; }
    /// <summary>Raw start instant, exposed so the Date grid column can sort by true chronological
    /// age (SortMemberPath) rather than by the formatted DateDisplay string (Stage 5.3 polish).</summary>
    public DateTimeOffset StartedAtUtc { get; }
    public string DurationDisplay { get; }
    public bool IsRecovered { get; }
    public bool IsEdited { get; }
    public bool IsDiarised { get; }
    public bool IsSystemMix { get; }
    /// <summary>Non-null only when IsSystemMix (final-review FIX 2): mirrors the pre-Stage-5.3
    /// badge, which was only ever rendered under Visibility="{Binding IsSystemMix}" - so a
    /// genuine per-app row (no fallback) never showed this text. Now that the Source column's
    /// ElementStyle applies a ToolTip to EVERY row unconditionally, the null here is load-bearing:
    /// without it, a per-app row would show the else-branch text ("fell back to system mix") even
    /// though it never fell back - a false evidentiary claim. WPF renders no tooltip for a null
    /// ToolTip binding, reproducing the old Visibility gate.</summary>
    public string? SystemMixTooltip { get; }
    public string Source { get; }
    public bool IsArchived { get; }
    public bool IsPendingRecovery { get; }
    public IReadOnlyList<string> MatterIds { get; }
    public IReadOnlyList<MatterChip> MatterChips { get; }
    public SessionListItem Item { get; }

    public SessionRowViewModel(SessionListItem item, TimeProvider time,
        IReadOnlyDictionary<string, (string? Reference, string Name)>? matterLookup = null)
    {
        Item = item;
        var session = item.Session;
        var meta = item.Meta;

        Id = item.Id;
        Title = meta.Title;
        string app = session.App.ToString();
        string medium = meta.Medium.ToString();
        AppMedium = string.Equals(app, medium, StringComparison.OrdinalIgnoreCase)
            ? app : app + " / " + medium;

        // Session-local wall time exactly as SessionWriter renders projections (spec 1.2):
        // the stored DST-resolved offset when present; machine zone only for pre-v3 records.
        var startedLocal = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        DateDisplay = startedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var viewerLocal = TimeZoneInfo.ConvertTime(session.StartedAtUtc, time.LocalTimeZone);
        DateTooltip = "Your local time: "
            + viewerLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        StartedAtUtc = session.StartedAtUtc;

        // 3.1: endedAtUtc == null means the recovery scan has not finalized this session yet.
        IsPendingRecovery = session.EndedAtUtc is null;
        var span = TimeSpan.FromMilliseconds(session.DurationMs);
        DurationDisplay = IsPendingRecovery
            ? ""
            : span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

        IsRecovered = session.Recovered;
        IsEdited = meta.Edited;
        IsDiarised = session.Diarised;
        // 3.2: chosen system-mix has identical bleed characteristics to a fallback - both badge.
        IsSystemMix = session.Devices.Remote.Mode == RemoteMode.SystemMix
                      || session.Devices.Remote.FellBackToSystemMix;
        SystemMixTooltip = !IsSystemMix ? null
            : session.Devices.Remote.Mode == RemoteMode.SystemMix
                ? "System mix was the selected capture mode; other app audio may be included"
                : "Per-app capture fell back to system mix; other app audio may be included";
        Source = AppMedium + (IsSystemMix ? " \u2014 system mix" : " \u2014 per-app");
        IsArchived = meta.Archived;
        MatterIds = meta.MatterIds;
        // Stage 5.3 Task 4: `{ref} {name}` / `{id}-{ref} {name}` when the matter has a reference,
        // else `{name}` / `{id} {name}`; a lookup miss (deleted matter, lingering tag) falls back
        // to the raw id for both fields, mirroring SessionsPageViewModel.MatterLabel's fallback.
        MatterChips = MatterIds.Select(id =>
        {
            if (matterLookup is not null && matterLookup.TryGetValue(id, out var m))
            {
                string text = m.Reference is { Length: > 0 } r ? $"{r} {m.Name}" : m.Name;
                string full = m.Reference is { Length: > 0 } r2 ? $"{id}-{r2} {m.Name}" : $"{id} {m.Name}";
                return new MatterChip(text, full);
            }
            return new MatterChip(id, id);   // unknown id -> raw id
        }).ToArray();
    }
}

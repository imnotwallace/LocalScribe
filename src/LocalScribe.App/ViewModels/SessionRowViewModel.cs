using System.Globalization;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.ViewModels;

/// <summary>Immutable presentation row over one catalog entry (design 3.2). All display strings
/// are computed once at construction; a refresh rebuilds rows rather than mutating them.</summary>
public sealed class SessionRowViewModel
{
    public string Id { get; }
    public string Title { get; }
    public string AppMedium { get; }
    public string DateDisplay { get; }
    public string DateTooltip { get; }
    public string DurationDisplay { get; }
    public bool IsRecovered { get; }
    public bool IsEdited { get; }
    public bool IsDiarised { get; }
    public bool IsSystemMix { get; }
    public string SystemMixTooltip { get; }
    public string Source { get; }
    public bool IsArchived { get; }
    public bool IsPendingRecovery { get; }
    public IReadOnlyList<string> MatterIds { get; }
    public SessionListItem Item { get; }

    public SessionRowViewModel(SessionListItem item, TimeProvider time)
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
        SystemMixTooltip = session.Devices.Remote.Mode == RemoteMode.SystemMix
            ? "System mix was the selected capture mode; other app audio may be included"
            : "Per-app capture fell back to system mix; other app audio may be included";
        Source = AppMedium + (IsSystemMix ? " \u2014 system mix" : " \u2014 per-app");
        IsArchived = meta.Archived;
        MatterIds = meta.MatterIds;
    }
}

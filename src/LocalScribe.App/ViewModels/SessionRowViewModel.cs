using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
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
public sealed partial class SessionRowViewModel : ObservableObject
{
    /// <summary>The ONE mutable field on this otherwise-immutable row (design 2026-07-13 section
    /// 2.2 surface 2): the single content-match snippet line the Sessions quick filter shows under
    /// the title when the filter text matched this session's transcript content. Stamped
    /// exclusively by SessionsPageViewModel's content-filter pass; null hides the line. Every
    /// display STRING above stays computed-once (a refresh still replaces the whole object).</summary>
    [ObservableProperty] private string? _contentSnippet;

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
    /// <summary>Tooltip for the Source cell: the full Source text (so a character-ellipsis
    /// truncation stays recoverable on hover) plus, for a system-mix row, the evidentiary caveat
    /// (final-review FIX 2). Non-null for every row - a per-app row shows just its accurate label
    /// ("Webex - per-app"), never the false "fell back" claim the raw SystemMixTooltip guards against.</summary>
    public string SourceTooltip { get; }
    public bool IsArchived { get; }
    public bool IsPendingRecovery { get; }
    /// <summary>True only for the just-stopped session whose background finalize is still in flight
    /// (design 2026-07-12 section 4): the row exists on disk with EndedAtUtc still null, but this is a
    /// normal post-Stop finalize, not a crash recovery. Drives the "Finalizing..." chip.</summary>
    public bool IsFinalizing { get; }
    /// <summary>The "Recovering..." chip condition: pending on disk AND not the in-flight finalize.
    /// Splitting it off IsPendingRecovery keeps every existing gate (archive/open/export/delete) that
    /// reads IsPendingRecovery working unchanged while the chip no longer mislabels a normal finalize.</summary>
    public bool IsRecovering => IsPendingRecovery && !IsFinalizing;
    /// <summary>True while the shared RetranscriptionRunner is generating a new transcript
    /// version for THIS session (design 2026-07-13 section 3.4). Drives the "Re-transcribing..."
    /// chip; flips on/off through the same UpsertRowAsync in-place seam as IsFinalizing.</summary>
    public bool IsRetranscribing { get; }
    public IReadOnlyList<string> MatterIds { get; }
    public IReadOnlyList<MatterChip> MatterChips { get; }
    public SessionListItem Item { get; }

    public SessionRowViewModel(SessionListItem item, TimeProvider time,
        IReadOnlyDictionary<string, (string? Reference, string Name)>? matterLookup = null,
        bool isFinalizing = false, bool isRetranscribing = false)
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
        IsFinalizing = isFinalizing;
        IsRetranscribing = isRetranscribing;
        var span = TimeSpan.FromMilliseconds(session.DurationMs);
        DurationDisplay = IsPendingRecovery
            ? ""
            : span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

        IsRecovered = session.Recovered;
        IsEdited = meta.Edited;
        IsDiarised = session.Diarised;
        if (session.Origin == "imported")
        {
            // Audio import (design 2026-07-13 section 4.4): an imported row's Source is its
            // provenance ("Imported - MP3"), never a capture-mode claim - no mic/loopback ever
            // ran, so the per-app/system-mix labels (and their evidentiary caveats) would be
            // false statements about how this audio was obtained.
            IsSystemMix = false;
            SystemMixTooltip = null;
            // Prefer the copied file's own extension; fall back to ffprobe's format_name, which for
            // MP4-family audio is a joined list ("mov,mp4,m4a,...") - take the first token (B3-7),
            // mirroring the import dialog's FormatDisplay, so the Source cell shows one friendly label.
            string ext = Path.GetExtension(session.ImportedSource?.FileName ?? "").TrimStart('.');
            string fmt = (ext.Length > 0
                    ? ext
                    : (session.ImportedSource?.ContainerFormat ?? "").Split(',')[0])
                .ToUpperInvariant();
            Source = fmt.Length == 0 ? "Imported" : $"Imported \u2014 {fmt}";
            SourceTooltip = session.ImportedSource is { FileName.Length: > 0 } src
                ? $"{Source}\nOriginal file: {src.FileName}" : Source;
        }
        else
        {
            // 3.2: chosen system-mix has identical bleed characteristics to a fallback - both badge.
            IsSystemMix = session.Devices.Remote.Mode == RemoteMode.SystemMix
                          || session.Devices.Remote.FellBackToSystemMix;
            SystemMixTooltip = !IsSystemMix ? null
                : session.Devices.Remote.Mode == RemoteMode.SystemMix
                    ? "System mix was the selected capture mode; other app audio may be included"
                    : "Per-app capture fell back to system mix; other app audio may be included";
            Source = AppMedium + (IsSystemMix ? " \u2014 system mix" : " \u2014 per-app");
            SourceTooltip = SystemMixTooltip is null ? Source : Source + "\n" + SystemMixTooltip;
        }
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

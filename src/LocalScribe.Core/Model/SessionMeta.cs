using System.Globalization;
using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Model;

/// <summary>meta.json - user-owned truth (spec section 1.4). The only file user metadata edits touch.</summary>
public sealed record SessionMeta
{
    public int SchemaVersion { get; init; } = 2;
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public Medium Medium { get; init; }
    public IReadOnlyList<string> MatterIds { get; init; } = [];
    public IReadOnlyList<SessionParticipant> Participants { get; init; } = [];

    /// <summary>Declared Local voice count - pipeline-facing (diarisation ForcedClusterCount,
    /// NameResolver tier-2). Invariant (Stage 5.4 section 5.2): once the Session Details editor
    /// commits, this EQUALS the side's participant-slot count (Named + Unnamed). Unmigrated
    /// pre-5.4 sessions may have count > named rows with no unnamed rows on disk; consumers
    /// keep reading this integer and must never require unnamed rows to exist.</summary>
    public int LocalCount { get; init; } = 1;

    /// <summary>Declared Remote voice count - same contract and invariant as LocalCount.</summary>
    public int RemoteCount { get; init; } = 1;
    public string? SummaryRef { get; init; }
    public DateTimeOffset? SummaryGeneratedAtUtc { get; init; }
    public string? SummaryModel { get; init; }
    public bool Edited { get; init; }
    public DateTimeOffset? LastEditedAtUtc { get; init; }

    /// <summary>v2 (Stage 4): hidden from default views only - nothing leaves disk (design 1).</summary>
    public bool Archived { get; init; }

    /// <summary>Fresh meta at session start: title/medium derived from the system app,
    /// self auto-filled as the Local "Me" participant (spec section 1.4/section 8/section 10).</summary>
    public static SessionMeta CreateDefault(AppKind app, DateTimeOffset startedAtLocal, SessionParticipant? self)
        => new()
        {
            Title = string.Create(CultureInfo.InvariantCulture,
                $"{app} \u2014 {startedAtLocal:yyyy-MM-dd HH:mm}"),
            Medium = Enum.TryParse(app.ToString(), out Medium m) ? m : Medium.Other,
            Participants = self is null ? [] : [self],
        };
}

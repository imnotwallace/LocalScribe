using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

public sealed record SessionStartInfo(string Id, SessionMeta Meta, SessionRecord LiveRecord);

/// <summary>Shared session-start bootstrap (spec 1.2/1.4/9): wall-clock + timezone capture,
/// self participant from settings, default meta.json, collision-safe folder id, and the
/// recovery-compatible live session.json (EndedAtUtc == null). Used by both the offline
/// runner and the live SessionController; only the frame source differs between them.</summary>
public static class SessionBootstrap
{
    public static async Task<SessionStartInfo> StartAsync(StoragePaths paths, Settings settings,
        AppKind app, IReadOnlyList<SourceKind> sources, DeviceSnapshot devices,
        TimeProvider time, string appVersion, CancellationToken ct,
        IReadOnlyList<string>? matterIds = null)
    {
        var startedUtc = time.GetUtcNow();
        var tz = time.LocalTimeZone;
        var offset = tz.GetUtcOffset(startedUtc);
        var startedLocal = startedUtc.ToOffset(offset);

        SessionParticipant? self = string.IsNullOrEmpty(settings.Self.Name) ? null
            : new SessionParticipant
            { Id = "p-self", Name = settings.Self.Name, Role = settings.Self.Role, Side = SourceKind.Local, IsSelf = true };
        var meta = SessionMeta.CreateDefault(app, startedLocal, self) with { MatterIds = matterIds ?? [] };

        string id = SessionId.EnsureUnique(
            SessionId.New(startedLocal, app, meta.Title),
            x => Directory.Exists(paths.SessionDir(x)));
        Directory.CreateDirectory(paths.SessionDir(id));
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(meta, ct);

        var live = new SessionRecord
        {
            Id = id, App = app, StartedAtUtc = startedUtc,
            TimeZoneId = tz.Id, UtcOffsetMinutes = (int)offset.TotalMinutes,
            Sources = sources, AppVersion = appVersion, Language = settings.Language,
            Devices = devices,
        };
        await new SessionStore(paths.SessionJson(id)).SaveAsync(live, ct);
        return new SessionStartInfo(id, meta, live);
    }
}

namespace LocalScribe.App.Services;

/// <summary>The Record console's per-session matter selection (Stage 6.2), mirroring
/// RemoteAppOverride: the picker writes it, SessionViewModel reads it at Start to seed
/// LiveSessionOptions.MatterIds, and it is NEVER persisted to settings.json. Reverts to empty
/// when a session ends (the console clears it on Idle), so the next recording starts untagged
/// unless re-picked. Written from the UI thread, read at Start; volatile for visibility.</summary>
public sealed class MatterSelectionOverride
{
    private volatile IReadOnlyList<string> _matterIds = [];
    public IReadOnlyList<string> MatterIds { get => _matterIds; set => _matterIds = value ?? []; }
}

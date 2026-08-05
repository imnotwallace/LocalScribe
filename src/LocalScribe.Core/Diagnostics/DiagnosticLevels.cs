namespace LocalScribe.Core.Diagnostics;

/// <summary>The four level names Settings.Logging.Level has documented since v1
/// (docs/specs/localscribe-specs.md:871: "error|warn|info|debug") and their ordering. Read by
/// DiagnosticLog.Write, which is the FIRST production code ever to read that setting - the record
/// existed from v1 with zero readers (Tier 1 plan A, 2026-08-05).</summary>
public static class DiagnosticLevels
{
    public const string Error = "error";
    public const string Warn = "warn";
    public const string Info = "info";
    public const string Debug = "debug";

    /// <summary>Lower is more severe. An unrecognised value ranks as info - settings.json is
    /// hand-editable and a typo must degrade to the documented default, never silence the log
    /// (rank 0 would have been fail-quiet) and never flood it (rank 3).</summary>
    public static int Rank(string? level) => (level ?? "").Trim().ToLowerInvariant() switch
    {
        Error => 0,
        Warn => 1,
        Info => 2,
        Debug => 3,
        _ => 2,
    };
}

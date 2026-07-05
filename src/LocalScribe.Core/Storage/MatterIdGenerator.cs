using System.Globalization;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Matter id minting (design 4.2, spec section 1.5): "M-{yyyyMMdd}-{NNN}", sequential
/// within the day as max(existing NNN in the index)+1, then incrementing NNN until BOTH
/// the index id and the matters/&lt;id&gt;/ folder are free - the id doubles as the folder
/// name, and an orphan folder outside the index (MatterStore's documented crash window)
/// must never be reissued. Forward-only: legacy "M-{yyyy}-{NNN}" ids from before this change
/// are never renamed or reissued, and never match the day-scoped prefix (which always ends in
/// "-" after 8 digits, so "M-20260705-" cannot equal-prefix "M-2026-050"). Invariant culture:
/// ids are stable across machine calendars.</summary>
public static class MatterIdGenerator
{
    public static string Next(MattersIndex index, string mattersDir, DateOnly date)
    {
        string prefix = string.Create(CultureInfo.InvariantCulture, $"M-{date:yyyyMMdd}-");
        int max = 0;
        foreach (var entry in index.Matters)
        {
            if (entry.Id.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(entry.Id.AsSpan(prefix.Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int n)
                && n > max)
            {
                max = n;
            }
        }
        for (int nnn = max + 1; ; nnn++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{prefix}{nnn:D3}");
            if (index.Matters.All(e => e.Id != candidate)
                && !Directory.Exists(Path.Combine(mattersDir, candidate)))
            {
                return candidate;
            }
        }
    }
}

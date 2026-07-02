using System.Globalization;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Matter id minting (design 4.2, spec section 1.5): "M-{yyyy}-{NNN}", sequential
/// within the year as max(existing NNN in the index)+1, then incrementing NNN until BOTH
/// the index id and the matters/&lt;id&gt;/ folder are free - the id doubles as the folder
/// name, and an orphan folder outside the index (MatterStore's documented crash window)
/// must never be reissued. Invariant culture: ids are stable across machine calendars.</summary>
public static class MatterIdGenerator
{
    public static string Next(MattersIndex index, string mattersDir, int year)
    {
        string prefix = string.Create(CultureInfo.InvariantCulture, $"M-{year:D4}-");
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

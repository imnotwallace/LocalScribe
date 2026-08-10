using System.IO;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Services;

/// <summary>One downloadable component, pinned (Tier 1 plan D, T1-10, 2026-08-05). Sha256 and
/// Bytes are MACHINE-DERIVED by tools/fetch-models.ps1 -WriteComponentManifest from each file's
/// Hugging Face LFS pointer, never hand-typed - a mistyped pin would fail closed and delete a
/// perfectly good multi-gigabyte download with no way for the user to tell why.
///
/// License is carried per component and SURFACED IN THE UI BEFORE THE DOWNLOAD STARTS, which the
/// 2026-08-06 packaging design note (decision 5) requires in as many words: the weights this
/// panel fetches are not all under the same terms, and shipping Gemma weights silently is a
/// licensing question rather than a technical one. It is defaulted rather than required so an
/// older manifest without the field still loads and simply states no licence.</summary>
public sealed record ComponentPin(string Id, string Name, string File, string Url,
    string Sha256, long Bytes, string? License = null);

/// <summary>models/component-manifest.json, written by tools/fetch-models.ps1 and copied beside
/// the binary by build.ps1.</summary>
public sealed record ComponentManifest(int SchemaVersion, IReadOnlyList<ComponentPin> Components);

/// <summary>Loads the pin list (Tier 1 plan D, T1-10, 2026-08-05). Absence is NOT an error: a
/// build that shipped without the manifest simply offers no downloads, and the Components panel
/// still renders every probe-only row. Fail-soft here, fail-CLOSED in the helper - a missing pin
/// list must not become a download with no verification.</summary>
public static class ComponentCatalog
{
    public const string FileName = "component-manifest.json";

    public static async Task<IReadOnlyList<ComponentPin>> LoadAsync(string modelsRoot,
        CancellationToken ct)
    {
        string path = Path.Combine(modelsRoot, FileName);
        if (!System.IO.File.Exists(path)) return [];
        try
        {
            var manifest = await JsonFile.ReadAsync<ComponentManifest>(path, ct);
            // A newer schema is IGNORED, not mangled: this list only ever adds a Download button,
            // so degrading to "no downloads offered" is safe, unlike a store over evidence.
            return manifest is { SchemaVersion: 1 } ? manifest.Components : [];
        }
        catch (Exception) { return []; }
    }
}

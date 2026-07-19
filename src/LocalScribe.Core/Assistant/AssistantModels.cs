using System.Security.Cryptography;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>One installed, hash-verified GGUF model (design 2026-07-18 section 7.2).
/// LOCKED contract - feat/matter-qa consumes this record. FilePath is absolute.</summary>
public sealed record AssistantModelInfo(string CanonicalName, string FilePath, string Sha256, int NativeCtx, string License);

/// <summary>Artifact provenance (design 7.3 model{file,sha256,backend}): the model FILE NAME,
/// its pinned hash, and the backend ACTUALLY used (from AssistantDone - floor-fall discipline).
/// LOCKED contract - stored inside SummaryVersion and matter-qa chat artifacts.</summary>
public sealed record AssistantModelRef(string File, string Sha256, string Backend);

/// <summary>On-disk manifest entry, written by tools/fetch-models.ps1 into
/// models/assistant-manifest.json (design 7.2 {canonicalName, file, sha256, nativeCtx, license}).</summary>
public sealed record AssistantManifestEntry
{
    public string CanonicalName { get; init; } = "";
    public string File { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public int NativeCtx { get; init; }
    public string License { get; init; } = "";
}

public sealed record AssistantManifestFile
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<AssistantManifestEntry> Models { get; init; } = [];
}

/// <summary>The verified view of installed assistant models. LOCKED contract surface:
/// Installed + DefaultModel + the LoadAsync factory. Verify-on-load is deliberate (locked
/// rule: manifest with sha256; verify-on-load): a corrupt/tampered GGUF is EXCLUDED with a
/// note, never offered - stricter than whisper weights, which have no on-load hash today.</summary>
public sealed class AssistantModelManifest
{
    /// <summary>Design decisions log: default model LOCKED, no bake-off.</summary>
    public const string DefaultCanonicalName = "Qwen3-4B-Instruct-2507";
    public const int Version = 1;

    public IReadOnlyList<AssistantModelInfo> Installed { get; }
    public AssistantModelInfo? DefaultModel { get; }
    /// <summary>Human-readable reasons entries were excluded (surfaced degradation, never silent).</summary>
    public IReadOnlyList<string> Notes { get; }

    public AssistantModelManifest(IReadOnlyList<AssistantModelInfo> installed,
        AssistantModelInfo? defaultModel, IReadOnlyList<string> notes)
        => (Installed, DefaultModel, Notes) = (installed, defaultModel, notes);

    /// <summary>Loads models/assistant-manifest.json under modelsRoot and hash-verifies every
    /// entry's file. Missing manifest or empty models dir yields an EMPTY manifest (design 7.7:
    /// features off with explainer) - never a throw. Hashing is streamed; callers run this off
    /// the UI thread and cache via AssistantManifestCache.</summary>
    public static async Task<AssistantModelManifest> LoadAsync(string modelsRoot, CancellationToken ct)
    {
        string path = Path.Combine(modelsRoot, "assistant-manifest.json");
        var obj = await SchemaGuard.ReadObjectAsync(path, ct);
        if (obj is null) return new AssistantModelManifest([], null, []);
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "assistant-manifest.json");
        var file = await JsonFile.ReadAsync<AssistantManifestFile>(path, ct)
                   ?? new AssistantManifestFile();

        var installed = new List<AssistantModelInfo>();
        var notes = new List<string>();
        foreach (var entry in file.Models)
        {
            ct.ThrowIfCancellationRequested();
            string modelPath = Path.Combine(modelsRoot, entry.File);
            if (!System.IO.File.Exists(modelPath))
            { notes.Add($"{entry.File}: missing - run tools/fetch-models.ps1 -Assistant"); continue; }
            string actual;
            await using (var stream = System.IO.File.OpenRead(modelPath))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            { notes.Add($"{entry.File}: sha256 mismatch - file excluded (re-run the fetch script)"); continue; }
            installed.Add(new AssistantModelInfo(entry.CanonicalName, modelPath,
                entry.Sha256.ToLowerInvariant(), entry.NativeCtx, entry.License));
        }
        var def = installed.FirstOrDefault(m => m.CanonicalName == DefaultCanonicalName)
                  ?? installed.FirstOrDefault();
        return new AssistantModelManifest(installed, def, notes);
    }
}

/// <summary>Process-wide once-per-load cache over LoadAsync (hash-verifying a multi-GB file
/// per call would be wasteful). Invalidate() forces a reload (Settings refresh path).</summary>
public sealed class AssistantManifestCache(Func<CancellationToken, Task<AssistantModelManifest>> load)
{
    private readonly object _lock = new();
    private Task<AssistantModelManifest>? _cached;

    public Task<AssistantModelManifest> GetAsync(CancellationToken ct)
    {
        lock (_lock) return _cached ??= load(ct);
    }

    public void Invalidate() { lock (_lock) _cached = null; }
}

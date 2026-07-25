namespace LocalScribe.Core.Search.Semantic;

/// <summary>The ONE formula for the method tag (voiceprint Method-gating convention): model file
/// name (no extension, lowercase) + "@" + stored dim. Shared by the helper (emits it on the wire)
/// and the service (staleness check) so the two can never drift.</summary>
public static class EmbeddingMethod
{
    public static string For(string modelPath, int dim)
        => Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant() + "@" + dim;
}

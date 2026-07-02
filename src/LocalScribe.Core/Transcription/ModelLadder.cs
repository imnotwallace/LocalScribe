namespace LocalScribe.Core.Transcription;

/// <summary>One-step model downgrade for VRAM-OOM / sustained-RTF pressure (spec section 3
/// auto-downgrade triggers). Preserves the .en suffix; null at the floor or for unknown names.</summary>
public static class ModelLadder
{
    private static readonly string[] Rungs = { "large-v3", "medium", "small", "base", "tiny" };

    public static string? Downgrade(string modelName)
    {
        bool en = modelName.EndsWith(".en", StringComparison.Ordinal);
        string stem = en ? modelName[..^3] : modelName;
        int i = Array.IndexOf(Rungs, stem);
        if (i < 0 || i == Rungs.Length - 1) return null;
        string next = Rungs[i + 1];
        return en ? next + ".en" : next;
    }
}

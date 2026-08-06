using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>The ONE composed "which engine produced this" string (Tier 1 T1-6, spec 2026-08-05
/// :66-72). The owner ruled on 2026-08-05 that the live model cap (small.en on CUDA, base.en on
/// Vulkan) is a deliberate realtime-factor decision and STAYS; what follows is that the divergence
/// from import's large-v3-turbo default must be DISCLOSED. Shared by the session-start transcript
/// marker and, through WhisperModelCatalog.AccuracyTier, by the ready-card chip and the export
/// metadata block - REJECTED: composing the sentence at each site, which is exactly the drift
/// MetadataFormat exists to prevent.</summary>
public static class EngineDisclosure
{
    /// <summary>"base.en (CPU), Basic accuracy" - or "distil-large-v3.5 (CUDA)" when the model is
    /// not in the catalog. The backend is upper-cased to match how PersistFinalAsync stores it in
    /// session.json and how the read-view footer renders it.</summary>
    public static string Line(string modelName, Backend backend)
    {
        string head = modelName + " (" + backend.ToString().ToUpperInvariant() + ")";
        string tier = WhisperModelCatalog.AccuracyTier(modelName);
        return tier.Length == 0 ? head : head + ", " + tier;
    }
}

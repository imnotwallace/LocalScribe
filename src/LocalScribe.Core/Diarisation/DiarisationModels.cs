namespace LocalScribe.Core.Diarisation;

/// <summary>The two sherpa ONNX model filenames every diarisation path resolves through
/// ModelPaths.Resolve. Hoisted here (design 2026-07-28 task 3) from three duplicated literals:
/// SplitSpeakersViewModel.RunAsync, SettingsPageViewModel.EmbeddingModelFile, and the import-time
/// detection step. The embedding name in particular MUST be one value everywhere - an enrollment
/// made under one file is stamped with a Method that can never match one made under another.</summary>
public static class DiarisationModels
{
    /// <summary>Segmentation model. A forward-slash SUBPATH: sherpa ships model.onnx inside a
    /// versioned folder, and Path.Combine handles the separator.</summary>
    public const string Segmentation = "sherpa-onnx-pyannote-segmentation-3-0/model.onnx";

    /// <summary>Speaker-embedding model (CAM++). Flat filename.</summary>
    public const string Embedding = "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";
}

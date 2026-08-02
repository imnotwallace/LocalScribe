namespace LocalScribe.App.ViewModels;

/// <summary>The single disabled row both disk-model pickers (Import, Re-transcribe) show when
/// no ggml model is on disk (UX round 2026-08-02 item 3.8): an empty ItemsSource paints a blank
/// ComboBox that reads as a bug; a selected-but-disabled explanatory row does not. The row is
/// injected as a passthrough WhisperModelInfo (empty subtitle, worst rank) so the catalog-shaped
/// pickers render it one-line. Start stays gated off in both dialogs while this is the only
/// entry. Single-sourced so the two dialogs' sentinels can never drift (the
/// SearchPage-vs-SessionsPage sentinel divergence lesson).</summary>
public static class ModelPickerSentinel
{
    public const string NoModelsFound = "(no models found)";
}

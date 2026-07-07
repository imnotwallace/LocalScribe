namespace LocalScribe.App.Services;

/// <summary>A Save-As request for the pickSavePath composition-root seam (design 3.4). The VM supplies
/// a default file name and a dialog filter; the App-side lambda wraps Microsoft.Win32.SaveFileDialog
/// and remembers the last-used directory in throwaway UI state (window-state.json, NOT settings.json).</summary>
public sealed record SavePathRequest(string DefaultFileName, string Filter);

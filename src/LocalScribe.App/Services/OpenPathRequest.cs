namespace LocalScribe.App.Services;

/// <summary>An Open-file request for the pickOpenPath composition-root seam (design 2026-07-13
/// section 4.4) - the SavePathRequest twin: the VM supplies the dialog filter; the App-side
/// lambda wraps Microsoft.Win32.OpenFileDialog.</summary>
public sealed record OpenPathRequest(string Filter);

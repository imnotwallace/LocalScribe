namespace LocalScribe.Core.Model;

/// <summary>What the session export dialog produces. Lives in Core (not the App view-model layer
/// it started in) because it became PERSISTED domain state in design 2026-08-04 section 4, and
/// Core cannot reference App. Persists as a string via JsonStringEnumConverter, the house pattern
/// AudioFormat/Backend/MicMode already follow.</summary>
public enum ExportFormat { Zip, Docx, Markdown, Text }

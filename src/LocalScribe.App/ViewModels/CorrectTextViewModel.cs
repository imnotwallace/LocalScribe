using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Projection;

namespace LocalScribe.App.ViewModels;

/// <summary>One editable constituent segment of the "Correct text..." dialog (Stage 6.1).
/// EditedText seeds from the DISPLAYED (projected) text - what the user sees is what they fix;
/// RawText shows the machine original alongside so a correction is never made blind to it.
/// RevertRequested (offered only for already-corrected segments) removes the overlay entry.</summary>
public sealed partial class CorrectionItemViewModel : ObservableObject
{
    public RowSegment Segment { get; }
    public string Stamp { get; }
    public string RawText => Segment.RawText;
    public bool IsCorrected => Segment.IsCorrected;
    [ObservableProperty] private string _editedText;
    [ObservableProperty] private bool _revertRequested;

    public CorrectionItemViewModel(RowSegment segment, string stamp)
    {
        (Segment, Stamp) = (segment, stamp);
        _editedText = segment.ProjectedText;
    }
}

/// <summary>Editor VM for the read view's "Correct text..." dialog (Stage 6.1, design section
/// 1.3). WPF-free. Diff-only: a correction is stored only for text that differs from what is
/// currently displayed, so an untouched dialog writes nothing; a whitespace-only correction is
/// blocked here (and again in EditStore) because transcript content is never removed. All disk
/// work goes through MaintenanceService (house rule).</summary>
public sealed partial class CorrectTextViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly IUiErrorReporter _reporter;
    private readonly string _versionId;

    public string SessionId { get; }
    public IReadOnlyList<CorrectionItemViewModel> Items { get; }
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _validationMessage = "";

    /// <summary><paramref name="versionId"/> is the version the caller (ReadViewViewModel) had
    /// LOADED when this dialog was opened (F1 fix, whole-branch review): SaveAsync targets exactly
    /// this version rather than letting MaintenanceService re-resolve ActiveVersion at write
    /// time, so a background re-transcription completing while this modal dialog is open cannot
    /// silently redirect the correction into the wrong version's overlay.</summary>
    public CorrectTextViewModel(MaintenanceService maintenance, IUiErrorReporter reporter,
        string sessionId, IReadOnlyList<RowSegment> segments, string timestampsMode,
        DateTimeOffset startedAtLocal, string versionId)
    {
        (_maintenance, _reporter, SessionId, _versionId) = (maintenance, reporter, sessionId, versionId);
        Items = segments.Select(s => new CorrectionItemViewModel(s,
            TimestampFormat.Stamp(s.StartMs, timestampsMode, startedAtLocal))).ToList();
    }

    /// <summary>True = done, close the dialog (including the nothing-changed case, which writes
    /// nothing). False = the dialog stays open: a validation problem (ValidationMessage set) or
    /// an IO failure (reported via IUiErrorReporter).</summary>
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        var corrections = new Dictionary<int, string>();
        var reverts = new List<int>();
        foreach (var item in Items)
        {
            if (item.RevertRequested && item.IsCorrected)
            {
                reverts.Add(item.Segment.Seq);
                continue;
            }
            string text = item.EditedText.Trim();
            // Trim BOTH sides so the no-op guard is self-contained: comparing trimmed input to an
            // un-trimmed ProjectedText would depend on the ingestion layer never emitting stray
            // whitespace, and if it ever did an untouched Save would write a phantom whitespace-only
            // "correction" (flipping the (edited) badge on lines the human never touched).
            if (text == item.Segment.ProjectedText.Trim()) continue;
            if (text.Length == 0)
            {
                ValidationMessage = "A correction cannot be empty - transcript content is never removed.";
                return false;
            }
            corrections[item.Segment.Seq] = text;
        }
        if (corrections.Count == 0 && reverts.Count == 0) return true;

        ValidationMessage = "";
        IsSaving = true;
        try
        {
            await _maintenance.SaveTextCorrectionsAsync(SessionId, corrections, reverts, _versionId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _reporter.Report("Save text corrections", ex);
            return false;
        }
        finally { IsSaving = false; }
    }
}

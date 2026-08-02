using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

namespace LocalScribe.App.ViewModels;

/// <summary>One reassignment target: a same-side session participant (identity-first, design
/// section 1.4) or an existing named speakers.json cluster not owned by any listed participant.</summary>
public sealed record ReassignCandidate(string Display, SpeakerPinTarget Target);

/// <summary>A checkbox row for one constituent segment. Disabled (and unchecked) for a segment
/// from the other stream - a rare mixed-source turn; candidates are side-scoped, so those lines
/// need their own visit from a row of that stream.</summary>
public sealed partial class SegmentChoiceViewModel : ObservableObject
{
    public RowSegment Segment { get; }
    public string Stamp { get; }
    public string Preview => Segment.ProjectedText;
    public bool IsEnabled { get; }
    [ObservableProperty] private bool _isChecked;

    public SegmentChoiceViewModel(RowSegment segment, string stamp, bool isEnabled)
    {
        (Segment, Stamp, IsEnabled) = (segment, stamp, isEnabled);
        _isChecked = isEnabled;
    }
}

/// <summary>Editor VM for the read view's "Reassign speaker..." dialog (Stage 6.1, design
/// section 1.4). WPF-free. Candidates: the session's same-side NAMED participants first (picking
/// one pins to its owned clusterKey, minting + recording ownership when it has none), then named
/// clusters no participant owns. Creating a brand-new person stays in Session Details - the
/// no-candidates state points there via OpenSessionDetailsRequested (one identity-creation flow).</summary>
public sealed partial class ReassignSpeakerViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly IUiErrorReporter _reporter;
    private readonly string _versionId;

    public string SessionId { get; }
    public TranscriptSource Source { get; }
    public IReadOnlyList<ReassignCandidate> Candidates { get; }
    public IReadOnlyList<SegmentChoiceViewModel> Segments { get; }
    /// <summary>The subset of <see cref="Segments"/> shown after the search filter (2026-07-31).
    /// Holds the SAME objects as Segments, so a tick made while filtered persists when the filter
    /// changes - selections stack across successive searches. SaveAsync reads the full Segments
    /// list, so a line ticked under an earlier search is saved even when no longer shown.</summary>
    public ObservableCollection<SegmentChoiceViewModel> VisibleSegments { get; } = new();
    public bool HasCandidates => Candidates.Count > 0;
    [ObservableProperty] private ReassignCandidate? _selectedCandidate;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _validationMessage = "";
    /// <summary>Live search text (2026-07-31): lines whose stamp or preview contains it stay shown;
    /// empty shows all. UpdateSourceTrigger=PropertyChanged in XAML filters as the user types.</summary>
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private int _selectedCount;
    private int _enabledCount;
    /// <summary>"N of M selected" - M is the count of assignable (enabled) lines. Bulk reassign can
    /// seed hundreds, so the user needs a running tally the select-all/deselect-all + search drive.</summary>
    public string SelectionSummary => $"{SelectedCount} of {_enabledCount} selected";

    public event Action<string>? OpenSessionDetailsRequested;

    /// <summary><paramref name="versionId"/> is the version the caller (ReadViewViewModel) had
    /// LOADED when this dialog was opened (F1 fix, whole-branch review) - see
    /// CorrectTextViewModel's ctor doc for why SaveAsync must target exactly this version rather
    /// than a re-resolved ActiveVersion.</summary>
    public ReassignSpeakerViewModel(MaintenanceService maintenance, IUiErrorReporter reporter,
        string sessionId, TranscriptSource source, IReadOnlyList<RowSegment> segments,
        SessionMeta meta, Speakers? speakers, string timestampsMode, DateTimeOffset startedAtLocal,
        string versionId)
    {
        (_maintenance, _reporter, SessionId, Source, _versionId) =
            (maintenance, reporter, sessionId, source, versionId);

        SourceKind side = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        var candidates = new List<ReassignCandidate>();
        var ownedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in meta.Participants)
        {
            if (p.Side != side || p.Kind != ParticipantKind.Named || string.IsNullOrEmpty(p.Name))
                continue;
            candidates.Add(new ReassignCandidate(p.Name, new SpeakerPinTarget.Participant(p.Id)));
            if (p.ClusterKey is string ck) ownedKeys.Add(ck);
        }
        if (speakers is not null)
        {
            string prefix = source + ":";
            foreach (var (key, name) in speakers.Names)
                if (key.StartsWith(prefix, StringComparison.Ordinal) && !ownedKeys.Contains(key))
                    candidates.Add(new ReassignCandidate($"{name} (detected voice)",
                        new SpeakerPinTarget.Cluster(key)));
        }
        Candidates = candidates;

        Segments = segments.Select(s => new SegmentChoiceViewModel(s,
            TimestampFormat.Stamp(s.StartMs, timestampsMode, startedAtLocal),
            isEnabled: s.Source == source)).ToList();

        _enabledCount = Segments.Count(s => s.IsEnabled);
        foreach (var s in Segments)
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SegmentChoiceViewModel.IsChecked)) RecountSelected();
            };
        RecountSelected();
        ApplyFilter();
    }

    private void RecountSelected() => SelectedCount = Segments.Count(s => s.IsChecked && s.IsEnabled);

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SelectionSummary));

    /// <summary>Rebuilds <see cref="VisibleSegments"/> from <see cref="Segments"/> for the current
    /// <see cref="FilterText"/>. Rebuilt (not per-item Visibility) so the list is compact; the
    /// SAME item objects are re-added, so their IsChecked state survives every filter change.</summary>
    private void ApplyFilter()
    {
        VisibleSegments.Clear();
        foreach (var s in Segments)
            if (string.IsNullOrWhiteSpace(FilterText)
                || s.Preview.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || s.Stamp.Contains(FilterText, StringComparison.OrdinalIgnoreCase))
                VisibleSegments.Add(s);
    }

    /// <summary>Tick every ENABLED line currently shown (respects the search filter). A disabled
    /// other-stream line is left untouched - it can never be saved anyway.</summary>
    public void SelectAllShown()
    {
        foreach (var s in VisibleSegments) if (s.IsEnabled) s.IsChecked = true;
    }

    /// <summary>Untick every line currently shown (respects the search filter).</summary>
    public void DeselectAllShown()
    {
        foreach (var s in VisibleSegments) s.IsChecked = false;
    }

    public void RequestOpenSessionDetails() => OpenSessionDetailsRequested?.Invoke(SessionId);

    /// <summary>True = pinned, close the dialog. False = validation problem (ValidationMessage
    /// set) or IO failure (reported); the dialog stays open.</summary>
    public async Task<bool> SaveAsync(CancellationToken ct)
    {
        if (SelectedCandidate is null)
        {
            ValidationMessage = "Choose who actually spoke.";
            return false;
        }
        var seqs = Segments.Where(s => s.IsChecked && s.IsEnabled)
            .Select(s => s.Segment.Seq).ToList();
        if (seqs.Count == 0)
        {
            ValidationMessage = "Tick at least one line to reassign.";
            return false;
        }

        ValidationMessage = "";
        IsSaving = true;
        try
        {
            await _maintenance.SaveSpeakerPinsAsync(SessionId, Source, seqs,
                SelectedCandidate.Target, _versionId, ct);
            return true;
        }
        catch (Exception ex)
        {
            _reporter.Report("Reassign speaker", ex);
            return false;
        }
        finally { IsSaving = false; }
    }
}

// src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>A source offered in the Split-speakers dialog (design section 4.1/4.2): a source is
/// offered only when its declared count is > 1, it is in the session's RetainedAudioSources, and
/// its leg file actually probes present on disk. LegPath is resolved once at load time (the same
/// probe PlaybackViewModel.Resolve uses) so Run never needs to re-probe.</summary>
public sealed partial class SplitSourceOption(SourceKind source, int declaredCount, string legPath)
    : ObservableObject
{
    public SourceKind Source { get; } = source;
    public int DeclaredCount { get; } = declaredCount;
    public string LegPath { get; } = legPath;

    [ObservableProperty] private bool _selected;
}

/// <summary>A pick-able naming candidate for a diarised cluster (Stage 5.4 C2): one of the
/// session's NAMED speaker slots on the cluster's side, carrying participant identity so Confirm
/// can attach cluster ownership (ClusterKey) to the exact slot that was picked. ToString() returns
/// the display name so the editable ComboBox keeps committing plain text into
/// ClusterRowViewModel.Name - free typing stays possible, and a typed name matching no slot
/// behaves exactly as before (string into speakers.Names only).</summary>
public sealed record SpeakerCandidate(string ParticipantId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One diarised cluster offered for naming (design section 4.2). Name defaults to the
/// materialised <see cref="DefaultSpeakerLabels"/> label and is user-editable; blank on confirm
/// means "keep the default" (handled by the owning VM, not here).</summary>
public sealed partial class ClusterRowViewModel(
    string clusterKey, SourceKind source, int clusterId, string defaultName,
    IReadOnlyList<string> previewLines, long? snippetStartMs,
    IReadOnlyList<SpeakerCandidate> nameCandidates)
    : ObservableObject
{
    public string ClusterKey { get; } = clusterKey;
    public SourceKind Source { get; } = source;
    public int ClusterId { get; } = clusterId;
    public string DefaultName { get; } = defaultName;

    /// <summary>A few representative transcript utterances for this cluster (design 4.2 "name" step).</summary>
    public IReadOnlyList<string> PreviewLines { get; } = previewLines;

    /// <summary>This cluster's side's NAMED speaker slots (Stage 5.4 C2), offered as pick-able
    /// candidates in the naming ComboBox and carrying participant identity for the confirm-time
    /// ownership map. Feeds ItemsSource + confirm-time id resolution; free text for un-rostered
    /// speakers remains possible (IsEditable="True" on the ComboBox).</summary>
    public IReadOnlyList<SpeakerCandidate> NameCandidates { get; } = nameCandidates;

    /// <summary>Start (ms) of this cluster's earliest diarised segment on the source leg - what the
    /// window's play-button binding seeks to via the owning VM's PlaySnippet hook (design 4.2).
    /// Null when the cluster produced no raw segment (should not happen; defensive only).</summary>
    public long? SnippetStartMs { get; } = snippetStartMs;

    [ObservableProperty] private string _name = defaultName;
}

/// <summary>The Split-speakers dialog view model (Stage 5 design section 4). WPF-free: all
/// observable mutation that could originate off the UI thread routes through the injected
/// dispatch, and no DateTime.Now/Guid.NewGuid - TimeProvider only. Drives IDiarisationEngine per
/// selected source, applies the declared-count soft prior (auto first, optional forced re-run),
/// and confirms the run through MaintenanceService.SaveDiarisationAsync (the single write gate;
/// this VM never touches SpeakersStore/SessionStore directly).
///
/// Implements <see cref="IDisposable"/> (final-review fix): the dialog's window MUST cancel any
/// in-flight run on every close path - title-bar X, or WindowRegistry.CloseAllFor when a session
/// is deleted while this dialog is still open - not only via the Cancel button. Without this, a
/// closed dialog whose CancellationToken was never signalled leaves
/// LocalScribe.Diarizer.exe running as an orphaned CPU-bound child process and can hold the
/// session's FLAC leg open across a session-delete recycle. Dispose() reuses the same Cancel()
/// the button wires to, so ProcessDiarisationHelper.RunAsync's ct.Register callback kills the
/// child process tree exactly as the Cancel button does. WPF-free: Dispose only cancels a token,
/// nothing more.</summary>
public sealed partial class SplitSpeakersViewModel : ObservableObject, IDisposable
{
    private readonly IDiarisationEngine _engine;
    private readonly MaintenanceService _maintenance;
    private readonly StoragePaths _paths;
    private readonly ISettingsService _settings;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly Func<string, string> _resolveModel;

    private string _sessionId = "";
    /// <summary>F1 fix (whole-branch review): the version this dialog LOADED and read the
    /// cluster-to-line map from (session.ActiveVersion captured at LoadAsync time, LoadAsync's
    /// TranscriptStore read at line ~211). ConfirmAsync passes exactly this to
    /// MaintenanceService.SaveDiarisationAsync instead of letting it re-resolve ActiveVersion at
    /// write time, so a re-transcription completing while this dialog is open cannot silently
    /// redirect the commit into the wrong version's speakers.json.</summary>
    private string _versionId = TranscriptVersions.Root;
    private IReadOnlyList<TranscriptLine> _lines = [];
    // Per-side name candidates (design B2) for the cluster-naming ComboBox, computed once in
    // Apply() from loaded.Meta.Participants and threaded into each side's ClusterRowViewModel
    // when clusters are built in RunAsync. Feeds the dropdown only - never the confirm path.
    private IReadOnlyList<SpeakerCandidate> _localCandidates = Array.Empty<SpeakerCandidate>();
    private IReadOnlyList<SpeakerCandidate> _remoteCandidates = Array.Empty<SpeakerCandidate>();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // Per-source state kept across Run -> ForceCount -> Confirm. Not readonly: a successful
    // RunAsync/ForceCountCommand pass REPLACES both dictionaries wholesale (Task 8 review fix) so
    // a cancelled/thrown mid-loop run never leaves a partially-advanced mix of old and new sources.
    private Dictionary<SourceKind, DiarisationResult> _resultBySource = new();
    private Dictionary<SourceKind, ClusterAssignment> _assignmentBySource = new();

    [ObservableProperty] private bool _systemMixWarning;
    [ObservableProperty] private bool _countMismatch;
    [ObservableProperty] private bool _canForceCount;
    [ObservableProperty] private double _progress;
    /// <summary>True from the moment a Run/ForceCount pass starts until it settles (success,
    /// cancel, or error) - drives the commands' CanExecute so the UI cannot fire a second
    /// concurrent pass (Task 9 review: a stale CanForceCount from a PRIOR mismatched run must not
    /// let "Use N speakers" fire while a fresh Run is already in flight).</summary>
    [ObservableProperty] private bool _isRunning;
    /// <summary>Button text for the count-mismatch panel's force-rerun action, e.g. "Use 3
    /// speakers" (single mismatched source) or a per-source breakdown when more than one selected
    /// source mismatched. Recomputed alongside CountMismatch/CanForceCount at the end of a run.</summary>
    [ObservableProperty] private string _forceCountLabel = "";

    public ObservableCollection<SplitSourceOption> Sources { get; } = new();
    public ObservableCollection<ClusterRowViewModel> Clusters { get; } = new();

    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand ForceCountCommand { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }

    /// <summary>Hook the window wires to the dual audio player to play a representative snippet
    /// for a cluster (design 4.2). Left null-safe - the VM never assumes a window is attached.</summary>
    public Func<SourceKind, long, Task>? PlaySnippet { get; set; }

    /// <summary>Raised (dispatched) after a successful Confirm persisted the diarisation commit -
    /// the SplitSpeakers analogue of MetadataEditorViewModel.Saved. The composition root uses it
    /// to reload an open Session Details editor for this session from disk (safe: the editor is
    /// guaranteed CLEAN - DiariseCommand gates on !IsDirty, a LOCKED Stage 5.4 decision) and to
    /// refresh the Sessions grid row (Diarised flag). Not raised on a refused confirm or when the
    /// persist throws.</summary>
    public event Action<string>? DiarisationSaved;

    public SplitSpeakersViewModel(
        IDiarisationEngine engine,
        MaintenanceService maintenance,
        StoragePaths paths,
        ISettingsService settings,
        IUiErrorReporter reporter,
        Action<Action> dispatch,
        TimeProvider time,
        Func<string, string> resolveModel)
    {
        (_engine, _maintenance, _paths, _settings, _reporter, _dispatch, _time, _resolveModel)
            = (engine, maintenance, paths, settings, reporter, dispatch, time, resolveModel);

        // CanExecute predicates (Task 9, resolving a Task 8 deferred concern): gate the buttons,
        // not just the VM-internal guards, against premature clicks. AsyncRelayCommand.ExecuteAsync
        // - used directly by SplitSpeakersViewModelTests - bypasses CanExecute entirely, so these
        // predicates only affect real UI invocation (Command.Execute/ICommand.CanExecute), never
        // the existing tests.
        RunCommand = new AsyncRelayCommand(() => RunAsync(forceDeclaredCount: false), CanRun);
        ForceCountCommand = new AsyncRelayCommand(() => RunAsync(forceDeclaredCount: true), CanForceRun);
        CancelCommand = new RelayCommand(Cancel);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, CanConfirm);

        // Selecting/deselecting a source (checkbox toggle) and the Clusters list changing shape
        // both need to re-poke their dependent command's CanExecute; neither is itself an
        // ObservableProperty on this VM, so there is no source-generated notify for them.
        Sources.CollectionChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
        Clusters.CollectionChanged += (_, _) => ConfirmCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsRunning && Sources.Any(s => s.Selected);
    private bool CanForceRun() => !IsRunning && CanForceCount;
    private bool CanConfirm() => !IsRunning && Clusters.Count > 0;

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        ForceCountCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanForceCountChanged(bool value) => ForceCountCommand.NotifyCanExecuteChanged();

    private sealed record LoadedSession(SessionRecord Session, SessionMeta Meta,
        IReadOnlyList<TranscriptLine> Lines, List<SplitSourceOption> Sources);

    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        _sessionId = sessionId;
        try
        {
            var settings = _settings.Current;
            var loaded = await _maintenance.RunForSessionAsync(sessionId, async token =>
            {
                var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(token)
                              ?? throw new InvalidOperationException($"session.json missing for {sessionId}");
                var startedLocal = session.UtcOffsetMinutes is int offsetMin
                    ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
                    : session.StartedAtUtc.ToLocalTime();
                var meta = await new MetadataStore(_paths.MetaJson(sessionId)).LoadAsync(token)
                           ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
                // Versioned re-transcription (design 2026-07-13 section 3.3): the cluster-to-line
                // mapping must read the ACTIVE version's machine transcript (the audio legs are
                // version-independent; the committed speakers.json below already routes through
                // MaintenanceService's active-version resolution).
                var lines = await new TranscriptStore(
                    _paths.TranscriptJsonl(sessionId, session.ActiveVersion)).ReadAllAsync(token);

                var options = new List<SplitSourceOption>();
                // A source is splittable only when the session is finalized/recovered (design 4.1):
                // an in-progress session offers nothing at all, regardless of declared counts.
                if (session.EndedAtUtc is not null)
                {
                    string? local = ProbeLeg(sessionId, SourceKind.Local, session.RetainedAudioSources, settings.AudioFormat);
                    if (meta.LocalCount > 1 && local is not null)
                        options.Add(new SplitSourceOption(SourceKind.Local, meta.LocalCount, local));

                    string? remote = ProbeLeg(sessionId, SourceKind.Remote, session.RetainedAudioSources, settings.AudioFormat);
                    if (meta.RemoteCount > 1 && remote is not null)
                        options.Add(new SplitSourceOption(SourceKind.Remote, meta.RemoteCount, remote));
                }

                return new LoadedSession(session, meta, lines, options);
            }, ct);

            _dispatch(() => Apply(loaded));
        }
        catch (Exception ex) { _reporter.Report("Split speakers", ex); }
    }

    // Mirrors PlaybackViewModel.Resolve's probe: retained + on-disk format (preferred, then the
    // other format), so a session recorded before a format change still resolves its leg.
    private string? ProbeLeg(string sessionId, SourceKind kind,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
    {
        if (!retained.Contains(kind)) return null;
        string preferred = _paths.AudioFile(sessionId, kind, preferredFormat);
        if (File.Exists(preferred)) return preferred;
        var other = preferredFormat == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
        string alternate = _paths.AudioFile(sessionId, kind, other);
        return File.Exists(alternate) ? alternate : null;
    }

    private void Apply(LoadedSession loaded)
    {
        SystemMixWarning = loaded.Session.Devices.Remote.Mode == RemoteMode.SystemMix
                            || loaded.Session.Devices.Remote.FellBackToSystemMix;
        _versionId = loaded.Session.ActiveVersion;
        _lines = loaded.Lines;
        // Per-side identity-carrying candidates (Stage 5.4 C2): NAMED slots only - explicit
        // Unnamed slots (Group B's ParticipantKind) have no pickable name and are represented by
        // the declared count, not the picker. Blank-named rows are skipped defensively.
        _localCandidates = loaded.Meta.Participants
            .Where(p => p.Side == SourceKind.Local && p.Kind == ParticipantKind.Named
                        && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new SpeakerCandidate(p.Id, p.Name)).ToArray();
        _remoteCandidates = loaded.Meta.Participants
            .Where(p => p.Side == SourceKind.Remote && p.Kind == ParticipantKind.Named
                        && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new SpeakerCandidate(p.Id, p.Name)).ToArray();
        Sources.Clear();
        foreach (var s in loaded.Sources)
        {
            // Checkbox toggles mutate SplitSourceOption.Selected, not a VM-level property, so
            // RunCommand's CanExecute needs its own subscription per option to notice them.
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SplitSourceOption.Selected))
                    RunCommand.NotifyCanExecuteChanged();
            };
            Sources.Add(s);
        }
        Clusters.Clear();
        CountMismatch = false;
        CanForceCount = false;
        ForceCountLabel = "";
        Progress = 0;
        _resultBySource.Clear();
        _assignmentBySource.Clear();
    }

    // Synchronous IProgress: System.Progress<T> captures SynchronizationContext, which is
    // nondeterministic headless (house convention - see SettingsPageViewModel.DispatchedProgress).
    private sealed class DispatchedProgress(Action<Action> dispatch, Action<double> apply) : IProgress<double>
    {
        public void Report(double value) => dispatch(() => apply(value));
    }

    private async Task RunAsync(bool forceDeclaredCount)
    {
        var selected = Sources.Where(s => s.Selected).ToList();
        if (selected.Count == 0) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _dispatch(() => IsRunning = true);
        try
        {
            string segModel = _resolveModel("sherpa-onnx-pyannote-segmentation-3-0/model.onnx");
            string embModel = _resolveModel("3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx");

            bool anyMismatch = false;
            var freshClusters = new List<ClusterRowViewModel>();
            // Which selected sources' actual cluster count diverged from their declared count,
            // and by how much they declared - drives the count-mismatch panel's button text.
            var mismatched = new List<(SourceKind Source, int Declared)>();
            // Accumulate into locals, not the VM fields, for the whole loop (Task 8 review fix):
            // a cancel/throw partway through must leave _resultBySource/_assignmentBySource exactly
            // as any prior successful run left them, never a mix of old and newly-half-run sources.
            var newResultBySource = new Dictionary<SourceKind, DiarisationResult>();
            var newAssignmentBySource = new Dictionary<SourceKind, ClusterAssignment>();

            foreach (var source in selected)
            {
                int? forced = forceDeclaredCount ? source.DeclaredCount : null;
                var request = new DiarisationRequest(source.LegPath, source.Source, segModel, embModel, forced);
                var progress = new DispatchedProgress(_dispatch, p => Progress = p);

                var result = await _engine.DiariseAsync(request, progress, ct);
                newResultBySource[source.Source] = result;

                var assignment = ClusterAssigner.Assign(_lines, result.Segments, source.Source);
                newAssignmentBySource[source.Source] = assignment;

                int distinctClusters = assignment.ClusterKeys.Count;
                if (distinctClusters != source.DeclaredCount)
                {
                    anyMismatch = true;
                    mismatched.Add((source.Source, source.DeclaredCount));
                }

                foreach (string clusterKey in assignment.ClusterKeys)
                {
                    int clusterId = ParseClusterId(clusterKey);
                    string defaultName = DefaultSpeakerLabels.For(source.Source, clusterId);
                    var previews = PreviewLinesFor(source.Source, assignment, clusterKey);
                    long? snippetStartMs = result.Segments
                        .Where(s => s.Cluster == clusterId)
                        .Select(s => (long?)s.StartMs)
                        .DefaultIfEmpty(null)
                        .Min();
                    var candidates = source.Source == SourceKind.Local ? _localCandidates : _remoteCandidates;
                    freshClusters.Add(new ClusterRowViewModel(
                        clusterKey, source.Source, clusterId, defaultName, previews, snippetStartMs, candidates));
                }
            }

            // Only now - after every selected source ran to completion - replace the VM's
            // committed state, together with the UI-facing Clusters/CountMismatch/CanForceCount,
            // inside the SAME dispatch turn (Task 8 re-review fix). _dispatch is Dispatcher.
            // BeginInvoke - fire-and-forget - so writing the fields outside this block would let
            // _assignmentBySource jump ahead of Clusters, opening a window where a concurrent
            // ConfirmAsync passes its guard against the new assignment but still reads the stale
            // Clusters, producing a commit whose Assignments reference clusterKeys absent from
            // Names. A fresh run fully replaces prior state (no merge with stale sources).
            _dispatch(() =>
            {
                _resultBySource = newResultBySource;
                _assignmentBySource = newAssignmentBySource;
                Clusters.Clear();
                foreach (var c in freshClusters) Clusters.Add(c);
                CountMismatch = anyMismatch;
                // Force-N is suppressed for a system-mix leg (design 4.2): forcing exactly N
                // clusters could merge non-meeting/background audio into a real named speaker.
                CanForceCount = anyMismatch && !SystemMixWarning;
                ForceCountLabel = mismatched.Count switch
                {
                    0 => "",
                    1 => $"Use {mismatched[0].Declared} speakers",
                    _ => "Use declared counts (" +
                         string.Join(", ", mismatched.Select(m => $"{m.Source}: {m.Declared}")) + ")",
                };
                Progress = 1.0;
            });
        }
        catch (OperationCanceledException) { /* cancelled: nothing written, dialog stays put */ }
        catch (DiarisationException ex) { ReportDiarisationError(ex); }
        catch (Exception ex) { _reporter.Report("Split speakers", ex); }
        finally { _cts = null; _dispatch(() => IsRunning = false); }
    }

    // Up to 3 preview utterances (design 4.2 "a few representative utterances") for a cluster,
    // in transcript order.
    private IReadOnlyList<string> PreviewLinesFor(SourceKind source, ClusterAssignment assignment, string clusterKey)
    {
        var wanted = source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;
        var previews = new List<string>();
        foreach (var line in _lines)
        {
            if (previews.Count >= 3) break;
            if (line.Kind != TranscriptKind.Segment || line.Source != wanted) continue;
            if (!assignment.SeqToClusterKey.TryGetValue(line.Seq.ToString(), out string? key) || key != clusterKey) continue;
            previews.Add(line.Text);
        }
        return previews;
    }

    private static int ParseClusterId(string clusterKey)
    {
        int idx = clusterKey.IndexOf(':');
        return idx >= 0 && idx + 1 < clusterKey.Length && int.TryParse(clusterKey[(idx + 1)..], out int id) ? id : 0;
    }

    private void Cancel() => _cts?.Cancel();

    /// <summary>Cancels any in-flight Run/ForceCount pass (final-review fix): called by
    /// SplitSpeakersWindow.OnClosed on EVERY close path so a closed dialog never leaves the
    /// helper process running or the FLAC leg held open. Reuses the exact same <see cref="Cancel"/>
    /// the Cancel button calls. Idempotent - a second Dispose(), or one where no run is in flight
    /// (_cts already null, e.g. after a run already completed and settled), is a safe no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
    }

    private async Task ConfirmAsync()
    {
        var selected = Sources.Where(s => s.Selected).ToList();
        if (selected.Count == 0) return;
        // Precondition (Task 8 review fix): every selected source must have a completed run
        // recorded in _assignmentBySource. Without this, a selected-but-never-run source (or one
        // whose run was superseded by a later cancelled/failed pass) would sail through with an
        // empty assignment/method - persisting an incomplete "diarised" commit into speakers.json.
        if (selected.Any(s => !_assignmentBySource.ContainsKey(s.Source)))
        {
            _reporter.Info("Run diarisation for all selected sources before confirming.");
            return;
        }
        try
        {
            var sources = selected.Select(s => s.Source).ToList();
            var assignments = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            string method = "";
            foreach (var s in selected)
            {
                if (!_assignmentBySource.TryGetValue(s.Source, out var assignment)) continue;
                assignments[s.Source.ToString()] = assignment.SeqToClusterKey;
                if (_resultBySource.TryGetValue(s.Source, out var result)) method = result.Method;
            }

            var names = new Dictionary<string, string>();
            foreach (var cluster in Clusters)
                names[cluster.ClusterKey] = string.IsNullOrWhiteSpace(cluster.Name) ? cluster.DefaultName : cluster.Name;

            // Stage 5.4 C2: ownership map (participantId -> RAW clusterKey). A cluster whose
            // EFFECTIVE name (exactly the value written into names above) matches one of ITS OWN
            // side's identity-carrying candidates attaches that participant's ClusterKey; free
            // text matching no candidate stays speakers.Names-only (today's path). Last-wins if
            // the same participant is picked for two clusters (one ClusterKey field per slot).
            // SaveDiarisationAsync applies SpeakersMerge's collision remap before persisting, so
            // the raw keys here are safe to hand over. ALWAYS passed (possibly empty) so
            // un-reasserted stale ownership on a re-diarised side is cleared.
            var owned = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cluster in Clusters)
            {
                string effective = names[cluster.ClusterKey];
                var match = cluster.NameCandidates.FirstOrDefault(
                    c => string.Equals(c.Name, effective, StringComparison.Ordinal));
                if (match is not null) owned[match.ParticipantId] = cluster.ClusterKey;
            }

            var commit = new DiarisationCommit(sources, assignments, names, method, _time.GetUtcNow());
            await _maintenance.SaveDiarisationAsync(_sessionId, commit, _versionId, owned, CancellationToken.None);
            // Stage 5.4 C2 Task 3: only reached when the persist completed without throwing.
            _dispatch(() => DiarisationSaved?.Invoke(_sessionId));
        }
        catch (Exception ex) { _reporter.Report("Split speakers", ex); }
    }

    private void ReportDiarisationError(DiarisationException ex)
    {
        if (ex.Code == DiarisationErrorCode.ModelDownloadFailed)
        {
            _reporter.Report("Split speakers",
                new InvalidOperationException(
                    "Diarisation models are missing. Run tools/fetch-models.ps1, or set " +
                    "LOCALSCRIBE_MODELS to a folder containing them.", ex));
            return;
        }
        _reporter.Report("Split speakers", ex);
    }
}

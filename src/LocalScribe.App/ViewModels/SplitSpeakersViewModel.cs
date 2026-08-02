// src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>A source offered in the Split-speakers dialog (design section 4.1/4.2): a source is
/// offered when it is in the session's RetainedAudioSources AND its leg file actually probes
/// present on disk - the declared count is NOT an offering gate (design 2026-07-28 task 6;
/// SessionMeta.LocalCount/RemoteCount default to 1 and AudioImporter never raises them, so
/// gating on `> 1` made this dialog open EMPTY on every freshly imported session). DeclaredCount
/// is carried purely for the force-N button (see SplitSpeakersViewModel.CanForceRun), never as a
/// condition on whether this option exists at all. LegPath is resolved once at load time (the
/// same probe PlaybackViewModel.Resolve uses) so Run never needs to re-probe.</summary>
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
public sealed partial class ClusterRowViewModel : ObservableObject
{
    public string ClusterKey { get; }
    public SourceKind Source { get; }
    public int ClusterId { get; }
    public string DefaultName { get; }

    /// <summary>A few representative transcript utterances for this cluster (design 4.2 "name" step).</summary>
    public IReadOnlyList<string> PreviewLines { get; }

    /// <summary>This cluster's side's NAMED speaker slots (Stage 5.4 C2), offered as pick-able
    /// candidates in the naming ComboBox and carrying participant identity for the confirm-time
    /// ownership map. Feeds ItemsSource + confirm-time id resolution; free text for un-rostered
    /// speakers remains possible (IsEditable="True" on the ComboBox).</summary>
    public IReadOnlyList<SpeakerCandidate> NameCandidates { get; }

    /// <summary>Start (ms) of this cluster's earliest diarised segment on the source leg - what the
    /// window's play-button binding seeks to via the owning VM's PlaySnippet hook (design 4.2).
    /// Null when the cluster produced no raw segment (should not happen; defensive only).</summary>
    public long? SnippetStartMs { get; }

    // NotifyPropertyChangedFor(IsDefaultNamed) (Task 12 review fix): IsDefaultNamed is computed
    // off Name but raised no change notification of its own, so a binding on it (the "Remember
    // voice" enable-state) would go stale the instant the user edited the name.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultNamed))]
    private string _name;

    /// <summary>The one advisory voiceprint match for this cluster, or null for "no chip"
    /// (voiceprint design 2026-07-25). SUGGEST-ONLY: setting this never changes
    /// <see cref="Name"/> - only the user's explicit Accept does.</summary>
    [ObservableProperty] private VoiceprintSuggestion? _suggestion;

    /// <summary>User opt-in: on confirm, save this cluster's voiceprint under the typed name so
    /// future sessions can suggest them. Ignored for a row still carrying its default label.</summary>
    [ObservableProperty] private bool _rememberVoice;

    /// <summary>The person the user ACCEPTED a suggestion for, and that suggestion's score - the
    /// only source of confirm-time provenance. Cleared the moment the name is edited away from
    /// what was accepted (see <see cref="OnNameChanged"/>).
    ///
    /// Backed by SetProperty (Task 12 review fix), not a plain auto-property: both fields are
    /// mutated from code (AcceptSuggestionCommand, OnNameChanged), and the XAML "accepted" badge
    /// binds directly to them. A plain auto-property raises no PropertyChanged, so a name edit
    /// that clears the link would leave a stale "linked" badge showing on screen.</summary>
    private string? _acceptedPersonId;
    public string? AcceptedPersonId
    {
        get => _acceptedPersonId;
        private set => SetProperty(ref _acceptedPersonId, value);
    }

    private double? _acceptedScore;
    public double? AcceptedScore
    {
        get => _acceptedScore;
        private set => SetProperty(ref _acceptedScore, value);
    }

    public IRelayCommand AcceptSuggestionCommand { get; }
    public IRelayCommand DismissSuggestionCommand { get; }

    // The exact name written by the accept, so a later OnNameChanged can tell "the accept itself"
    // from "the user retyped the field".
    private string? _acceptedName;

    // An explicit constructor (not a primary one): the two commands close over `this`, which a
    // field/property initializer cannot do.
    public ClusterRowViewModel(
        string clusterKey, SourceKind source, int clusterId, string defaultName,
        IReadOnlyList<string> previewLines, long? snippetStartMs,
        IReadOnlyList<SpeakerCandidate> nameCandidates)
    {
        ClusterKey = clusterKey;
        Source = source;
        ClusterId = clusterId;
        DefaultName = defaultName;
        PreviewLines = previewLines;
        SnippetStartMs = snippetStartMs;
        NameCandidates = nameCandidates;
        _name = defaultName;

        AcceptSuggestionCommand = new RelayCommand(() =>
        {
            if (Suggestion is null) return;
            // Order matters: the Accepted* fields and _acceptedName must be in place BEFORE Name
            // is assigned, or OnNameChanged would see the accept as a manual edit and undo it.
            AcceptedPersonId = Suggestion.PersonId;
            AcceptedScore = Suggestion.Score;
            _acceptedName = Suggestion.PersonName;
            Name = Suggestion.PersonName;
            Suggestion = null;
        });
        DismissSuggestionCommand = new RelayCommand(() => Suggestion = null);
    }

    partial void OnNameChanged(string value)
    {
        // A manual edit after accept breaks the person link (the provenance/enrollment must
        // only ever describe what the user actually accepted).
        if (AcceptedPersonId is not null &&
            !string.Equals(value, _acceptedName, StringComparison.Ordinal))
        {
            AcceptedPersonId = null;
            AcceptedScore = null;
            _acceptedName = null;
        }
    }

    /// <summary>True while this row still shows its untouched default label (blank counts: the
    /// owning VM's confirm treats a blank name as "keep the default"). Gates the opt-in global
    /// search and the RememberVoice enrollment - neither may act on a nameless row.</summary>
    public bool IsDefaultNamed =>
        string.IsNullOrWhiteSpace(Name) || string.Equals(Name, DefaultName, StringComparison.Ordinal);
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
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly Func<string, string> _resolveModel;
    // Voiceprint seams (design 2026-07-25). All three are advisory/opt-in: every failure below
    // degrades to "no suggestions"/"nothing enrolled" and never blocks a run or a confirm.
    private readonly PeopleStore _people;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Matter>>> _loadMatters;
    private readonly VoiceprintEnrollmentService _enrollment;
    /// <summary>One-engine-at-a-time probe (design 2026-07-28 adjacent fix 3): non-null = a
    /// user-facing reason another heavy engine owns the machine right now. Null (or a null probe)
    /// means run. Probe-and-refuse, never a latch - the seam is deliberately cooperative
    /// (SessionController.cs:168-170, pinned by SessionControllerTests.cs:544-566).</summary>
    private readonly Func<string?>? _engineBusy;

    private string _sessionId = "";
    /// <summary>The session's matters (meta.MatterIds captured in <see cref="Apply"/>) - the scope
    /// of the DEFAULT suggestion pool. The global pool is only ever reached through the explicit
    /// <see cref="SearchAllPeopleCommand"/>.</summary>
    private IReadOnlyList<string> _matterIds = [];
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

    /// <summary>The vectors behind a HYDRATED set of rows (design 2026-07-28 task 7): this
    /// version's embeddings.json, keyed by full clusterKey. Written ONLY by <see cref="Apply"/> -
    /// the same turn that writes <c>_resultBySource</c> - so the identity check in
    /// <see cref="SearchAllPeopleAsync"/> covers both, and it can never survive into a state whose
    /// rows it does not describe. Null when nothing was committed for this version. Read only when
    /// <c>_resultBySource</c> is empty: a fresh run's own vectors always win, because cluster ids
    /// restart at 0 each run and the persisted entries can then describe a different voice.</summary>
    private ClusterEmbeddings? _hydratedEmbeddings;

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
    /// <summary>Explicit speaker-count input (2026-07-30): blank/invalid = Auto (threshold). A value
    /// >= 2 makes "Run with count" force EXACTLY that many clusters via
    /// DiarisationJob.ForcedClusterCount - the reliable escape from Auto over-clustering on real
    /// speech, and the ONLY count path that works for an imported session (whose declared count is 1
    /// or the wrong auto-committed count, so CanForceRun cannot serve it).</summary>
    [ObservableProperty] private string _speakerCountText = "";

    /// <summary>Dialog-local feedback, bound to this window's own InfoBar (2026-08-02 smoke fix,
    /// same trap as the read-view save fix): the shared IUiErrorReporter renders on MainWindow's
    /// InfoBar, which this separate dialog cannot show - so every guard refusal, failure and
    /// no-op acknowledgment looked silent here (Confirm/Save "looked like a dead button" on a
    /// hydrated dialog with unchanged names). Null = no status; cleared at the start of each
    /// Run/Confirm/Search attempt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusMessage;

    /// <summary>True renders the status InfoBar as Error; false as Informational.</summary>
    [ObservableProperty] private bool _statusIsError;

    /// <summary>The status InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// can't bind a null-check directly).</summary>
    public bool HasStatus => StatusMessage is not null;

    private void ShowStatus(string message, bool isError) =>
        _dispatch(() => { StatusMessage = message; StatusIsError = isError; });

    private void ClearStatus() =>
        _dispatch(() => { StatusMessage = null; StatusIsError = false; });

    public ObservableCollection<SplitSourceOption> Sources { get; } = new();
    public ObservableCollection<ClusterRowViewModel> Clusters { get; } = new();

    public IAsyncRelayCommand RunCommand { get; }
    public IAsyncRelayCommand RunWithCountCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand ForceCountCommand { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }

    /// <summary>Opt-in global match (voiceprint design 2026-07-25): re-matches this run's clusters
    /// against EVERY saved voiceprint, not just the session's matters' people. Deliberately never
    /// automatic, and it only fills rows the user has neither accepted nor named.</summary>
    public IAsyncRelayCommand SearchAllPeopleCommand { get; }

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
        Action<Action> dispatch,
        TimeProvider time,
        Func<string, string> resolveModel,
        PeopleStore people,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<Matter>>> loadMatters,
        VoiceprintEnrollmentService enrollment,
        Func<string?>? engineBusy = null)
    {
        (_engine, _maintenance, _paths, _settings, _dispatch, _time, _resolveModel)
            = (engine, maintenance, paths, settings, dispatch, time, resolveModel);
        (_people, _loadMatters, _enrollment) = (people, loadMatters, enrollment);
        _engineBusy = engineBusy;

        // CanExecute predicates (Task 9, resolving a Task 8 deferred concern): gate the buttons,
        // not just the VM-internal guards, against premature clicks. AsyncRelayCommand.ExecuteAsync
        // - used directly by SplitSpeakersViewModelTests - bypasses CanExecute entirely, so these
        // predicates only affect real UI invocation (Command.Execute/ICommand.CanExecute), never
        // the existing tests.
        RunCommand = new AsyncRelayCommand(() => RunAsync(forceDeclaredCount: false), CanRun);
        RunWithCountCommand = new AsyncRelayCommand(
            () => RunAsync(forceDeclaredCount: false, explicitCount: ParseForcedCount()), CanRunWithCount);
        ForceCountCommand = new AsyncRelayCommand(() => RunAsync(forceDeclaredCount: true), CanForceRun);
        CancelCommand = new RelayCommand(Cancel);
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, CanConfirm);
        SearchAllPeopleCommand = new AsyncRelayCommand(SearchAllPeopleAsync, CanSearchAllPeople);

        // Selecting/deselecting a source (checkbox toggle) and the Clusters list changing shape
        // both need to re-poke their dependent command's CanExecute; neither is itself an
        // ObservableProperty on this VM, so there is no source-generated notify for them.
        Sources.CollectionChanged += (_, _) =>
        {
            RunCommand.NotifyCanExecuteChanged();
            RunWithCountCommand.NotifyCanExecuteChanged();
            // Confirm depends on the selection too (fix round 1, I2), so it has to be re-poked
            // wherever Run is - here and in the per-option Selected handler in Apply.
            ConfirmCommand.NotifyCanExecuteChanged();
        };
        Clusters.CollectionChanged += (_, _) =>
        {
            ConfirmCommand.NotifyCanExecuteChanged();
            SearchAllPeopleCommand.NotifyCanExecuteChanged();
        };
    }

    private bool CanRun() => !IsRunning && Sources.Any(s => s.Selected);
    // Explicit-count run (2026-07-30): needs a selected source and a parseable count >= 2 (forcing
    // exactly 1 is meaningless - same rationale as CanForceRun). Independent of the declared count,
    // which is what makes this the escape hatch for an imported session whose declared count is 1
    // (or the wrong auto-committed count) - the case CanForceRun cannot serve.
    private int? ParseForcedCount() => int.TryParse(SpeakerCountText, out int n) && n >= 2 ? n : null;
    private bool CanRunWithCount() => !IsRunning && ParseForcedCount() is not null && Sources.Any(s => s.Selected);
    // Force-N needs a count somebody actually declared: forcing exactly 1 cluster is meaningless,
    // and 1 is the SessionMeta default nobody asserted (design 2026-07-28 task 6, now that the
    // declared count no longer gates whether a source is offered at all).
    private bool CanForceRun() => !IsRunning && CanForceCount
                                  && Sources.Any(s => s.Selected && s.DeclaredCount > 1);
    // The selection gate is real, not cosmetic (fix round 1, I2): ConfirmAsync returns silently
    // when nothing is ticked, and hydration makes "nothing ticked" the DEFAULT state on open - on
    // exactly the rename flow hydration exists to enable. It was reachable before (run, untick,
    // press Confirm); it is now the first thing a renaming user meets.
    private bool CanConfirm() => !IsRunning && Clusters.Count > 0 && Sources.Any(s => s.Selected);
    // Same !IsRunning gate as Confirm: a search reads this run's results and writes into the
    // Clusters rows, both of which a concurrent Run/ForceCount pass replaces wholesale.
    private bool CanSearchAllPeople() => !IsRunning && Clusters.Count > 0;

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        RunWithCountCommand.NotifyCanExecuteChanged();
        ForceCountCommand.NotifyCanExecuteChanged();
        ConfirmCommand.NotifyCanExecuteChanged();
        SearchAllPeopleCommand.NotifyCanExecuteChanged();
    }

    partial void OnSpeakerCountTextChanged(string value) => RunWithCountCommand.NotifyCanExecuteChanged();

    partial void OnCanForceCountChanged(bool value) => ForceCountCommand.NotifyCanExecuteChanged();

    /// <summary><paramref name="Committed"/>/<paramref name="Embeddings"/> are the hydration inputs
    /// (design 2026-07-28 task 7): the speakers overlay this dialog rebuilds its naming rows from,
    /// and the derived per-cluster vectors its suggestion chips come from. Both are read for
    /// <c>Session.ActiveVersion</c> - the same version <see cref="Apply"/> pins into
    /// <c>_versionId</c> and the write path commits to. <c>Committed</c> null = never diarised (or
    /// diarised on another version), which hydrates nothing at all; <c>Embeddings</c> null = no
    /// vectors on disk, which hydrates rows with no chips.</summary>
    private sealed record LoadedSession(SessionRecord Session, SessionMeta Meta,
        IReadOnlyList<TranscriptLine> Lines, List<SplitSourceOption> Sources,
        Speakers? Committed, ClusterEmbeddings? Embeddings);

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
                // an in-progress session offers nothing at all.
                //
                // The declared count is NOT a gate (design 2026-07-28 task 6). It used to be
                // `> 1`, which made this dialog open EMPTY on every freshly imported session:
                // SessionMeta.LocalCount/RemoteCount default to 1 (SessionMeta.cs:21,24) and
                // AudioImporter never raises them (AudioImporter.cs:108-110). The count remains
                // meaningful as the number the force-N button forces - see CanForceRun, which
                // suppresses forcing when nobody actually declared more than one voice.
                if (session.EndedAtUtc is not null)
                {
                    string? local = ProbeLeg(sessionId, SourceKind.Local, session.RetainedAudioSources, settings.AudioFormat);
                    if (local is not null)
                        options.Add(new SplitSourceOption(SourceKind.Local, meta.LocalCount, local));

                    string? remote = ProbeLeg(sessionId, SourceKind.Remote, session.RetainedAudioSources, settings.AudioFormat);
                    if (remote is not null)
                        options.Add(new SplitSourceOption(SourceKind.Remote, meta.RemoteCount, remote));
                }

                // Hydration (design 2026-07-28 task 7): the committed overlay for the version this
                // dialog is about to pin, so reopening it to rename a speaker never re-runs the
                // diariser. Read under the same gate hold as everything else here, and for the SAME
                // version Apply() captures into _versionId - a re-transcription landing mid-dialog
                // must not leave the rows describing one version and the commit hitting another.
                //
                // Not degraded to "no hydration" on failure, deliberately: speakers.json is the
                // sole speaker-name authority (Speakers.cs:4-5), not derived data, so a corrupt or
                // forward-versioned one throws straight out of SpeakersStore.LoadAsync and is
                // reported by the catch below. That is strictly kinder than opening normally -
                // SaveDiarisationAsync reads the very same file, so every confirm would fail
                // anyway, after the user had already spent minutes on a run that could never land.
                var committed = await new SpeakersStore(
                    _paths.SpeakersJson(sessionId, session.ActiveVersion)).LoadAsync(token);
                // Derived biometric data by contrast: absent/corrupt/forward-versioned all load
                // null out of ClusterEmbeddingsStore, which simply means "no suggestion chips".
                var embeddings = committed is null
                    ? null
                    : await new ClusterEmbeddingsStore(
                        _paths.EmbeddingsJson(sessionId, session.ActiveVersion)).LoadAsync(token);

                return new LoadedSession(session, meta, lines, options, committed, embeddings);
            }, ct);

            // Matched HERE, off the dispatch, exactly as RunAsync matches its own run's embeddings
            // (:616): the people.json/matters IO never touches the UI thread, and the answer is
            // already in hand when Apply publishes rows and chips together in one turn.
            var suggestions = await ComputeHydratedSuggestionsAsync(
                loaded.Embeddings, loaded.Meta.MatterIds, ct);

            _dispatch(() => Apply(loaded, suggestions));
        }
        catch (Exception ex)
        { ShowStatus("Couldn't load this session for splitting: " + ex.Message, isError: true); }
    }

    // Shared with the import-time detection step (design 2026-07-28): both must point the diariser
    // at the same file, so the probe lives in AudioLegProbe rather than being duplicated.
    private string? ProbeLeg(string sessionId, SourceKind kind,
        IReadOnlyList<SourceKind> retained, AudioFormat preferredFormat)
        => AudioLegProbe.Resolve(_paths, sessionId, kind, retained, preferredFormat);

    private void Apply(LoadedSession loaded, IReadOnlyDictionary<string, VoiceprintSuggestion> suggestions)
    {
        SystemMixWarning = loaded.Session.Devices.Remote.Mode == RemoteMode.SystemMix
                            || loaded.Session.Devices.Remote.FellBackToSystemMix;
        _versionId = loaded.Session.ActiveVersion;
        _lines = loaded.Lines;
        _matterIds = loaded.Meta.MatterIds;
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
                {
                    RunCommand.NotifyCanExecuteChanged();
                    RunWithCountCommand.NotifyCanExecuteChanged();
                    ForceCountCommand.NotifyCanExecuteChanged();
                    ConfirmCommand.NotifyCanExecuteChanged();   // fix round 1, I2
                }
            };
            Sources.Add(s);
        }
        Clusters.Clear();
        CountMismatch = false;
        CanForceCount = false;
        ForceCountLabel = "";
        Progress = 0;
        // Replaced wholesale rather than cleared in place, matching RunAsync's publish (:631-632):
        // SearchAllPeopleAsync snapshots _resultBySource by REFERENCE and re-checks identity before
        // applying (:784), so a reload must hand it a genuinely different object.
        _resultBySource = new Dictionary<SourceKind, DiarisationResult>();
        _assignmentBySource = new Dictionary<SourceKind, ClusterAssignment>();
        _hydratedEmbeddings = loaded.Embeddings;

        // Hydration (design 2026-07-28 task 7). Rebuild the naming rows from the committed overlay
        // with NO engine call, inside this same single dispatch turn - a row must never be
        // observable without its chip, nor Clusters without the assignment a concurrent confirm
        // would read. _resultBySource stays EMPTY on purpose: a hydrated row has no DiarisedSegment
        // list and no embedding vectors, and that emptiness is exactly what ConfirmAsync uses to
        // choose the rename-only write path. Committing these rows through
        // SaveDiarisationAsync/SpeakersMerge would be wrong, not merely wasteful - merge treats a
        // commit's keys as FRESH and remaps any that collide with a pinned or participant-owned key,
        // and on a rename the "fresh" keys ARE the existing keys, so a pinned cluster would collide
        // with itself and be duplicated under a new id.
        if (loaded.Committed is { } committed) HydrateClusters(committed, loaded.Meta, suggestions);

        // Auto-select the sources hydration just built rows for (design 2026-07-29 follow-up 2), so a
        // dialog reopened purely to rename has Confirm enabled without the user first ticking a box.
        // _assignmentBySource is populated only by hydration at load time, so this is exactly the
        // hydrated set and is empty on a never-diarised load (CanConfirm stays false there, as before).
        foreach (var s in Sources)
            if (_assignmentBySource.ContainsKey(s.Source))
                s.Selected = true;
    }

    /// <summary>Rebuilds <see cref="Clusters"/> and <c>_assignmentBySource</c> from an
    /// already-committed <paramref name="committed"/> overlay (design 2026-07-28 task 7). Runs on
    /// the dispatch thread, inside <see cref="Apply"/>'s single turn. Only ever produces rows for
    /// clusterKeys the overlay's Assignments actually reference: those are the keys a later rename
    /// hands to MaintenanceService.RenameSpeakersAsync, which by documented design does NOT verify
    /// that the keys it is given exist (unlike SaveSpeakerPinsAsync, it neither mints nor reuses),
    /// so nothing here may synthesise one.</summary>
    private void HydrateClusters(Speakers committed, SessionMeta meta,
        IReadOnlyDictionary<string, VoiceprintSuggestion> suggestions)
    {
        foreach (var source in new[] { SourceKind.Local, SourceKind.Remote })
        {
            // DiarisedSources, not Assignments, is the gate (fix round 1, I1).
            // EditStore.ReassignSpeakersAsync writes Assignments[source][seq] for a MANUAL PIN -
            // no diarisation, no Names entry, no DiarisedSources - so keying off Assignments alone
            // built phantom rows on a pins-only or partly-pinned session: labelled with
            // materialised defaults that contradict the read view, passing the never-run
            // precondition that used to refuse the confirm, and then taking the rename path, where
            // every key absent from Names is skipped - so the confirm wrote nothing at all.
            // DiarisedSources is already on disk and is exactly what tells a diarised source from
            // a pinned one.
            if (!committed.DiarisedSources.Contains(source)) continue;
            // speakers.json's outer Assignments key is the TranscriptSource string; Local/Remote
            // match (ClusterAssigner.cs:17-19).
            if (!committed.Assignments.TryGetValue(source.ToString(), out var seqToKey)) continue;
            if (seqToKey.Count == 0) continue;

            // Same shape ClusterAssigner.Assign produces (ClusterAssigner.cs:47-48): ids ascending,
            // so hydrated rows appear in the order a fresh run would have listed them.
            var assignment = new ClusterAssignment(
                new Dictionary<string, string>(seqToKey, StringComparer.Ordinal),
                seqToKey.Values.Distinct(StringComparer.Ordinal).OrderBy(ParseClusterId).ToList());
            _assignmentBySource[source] = assignment;

            var candidates = source == SourceKind.Local ? _localCandidates : _remoteCandidates;
            var wanted = source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;

            foreach (string clusterKey in assignment.ClusterKeys)
            {
                int clusterId = ParseClusterId(clusterKey);
                // A hydrated row has no DiarisedSegment list, so the snippet offset comes from the
                // earliest transcript line assigned to this cluster instead of the earliest raw
                // segment (:602-606) - within a segment by construction, and exactly what the play
                // button needs to seek to.
                long? snippetStartMs = _lines
                    .Where(l => l.Kind == TranscriptKind.Segment && l.Source == wanted
                                && assignment.SeqToClusterKey.TryGetValue(l.Seq.ToString(), out string? k)
                                && k == clusterKey)
                    .Select(l => (long?)l.StartMs)
                    .DefaultIfEmpty(null)
                    .Min();

                var row = new ClusterRowViewModel(
                    clusterKey, source, clusterId, DefaultSpeakerLabels.For(source, clusterId),
                    PreviewLinesFor(source, assignment, clusterKey), snippetStartMs, candidates);
                // Seed from the SAME precedence the read view renders (design 2026-07-29 follow-up 1):
                // NameResolver ranks the participant-ownership tier ABOVE the speakers.json overlay, so
                // a participant renamed in Session Details after diarisation would otherwise show a
                // STALE overlay name here - and confirming that stale row matches no candidate, clears
                // the owner's ClusterKey, and reverts the transcript. A null/blank result leaves the
                // row on its DefaultSpeakerLabels default (never the resolver's 0-based "Speaker N").
                if (NameResolver.ResolveClusterName(clusterKey, committed, meta) is { } seeded
                    && !string.IsNullOrWhiteSpace(seeded))
                    row.Name = seeded;
                // Chip only a row nobody has named yet - the same "never overwrite what the user
                // already decided" rule the opt-in global search applies (:788-790). A fresh run's
                // rows are all default-labelled when RunAsync stamps them (:636), so it needs no
                // such test; a hydrated row's name is a decision from an EARLIER confirm, and a
                // freshly imported one still carrying "Local Speaker 1" is exactly the case chips
                // are for. Stamped before the Add, so a row is never observable without its chip.
                if (row.IsDefaultNamed) row.Suggestion = suggestions.GetValueOrDefault(clusterKey);
                Clusters.Add(row);
            }
        }
    }

    // Synchronous IProgress: System.Progress<T> captures SynchronizationContext, which is
    // nondeterministic headless (house convention - see SettingsPageViewModel.DispatchedProgress).
    private sealed class DispatchedProgress(Action<Action> dispatch, Action<double> apply) : IProgress<double>
    {
        public void Report(double value) => dispatch(() => apply(value));
    }

    private async Task RunAsync(bool forceDeclaredCount, int? explicitCount = null)
    {
        ClearStatus();
        var selected = Sources.Where(s => s.Selected).ToList();
        if (selected.Count == 0) return;

        // Refuse rather than contend. Read at RUN time, not construction: a dialog opened while
        // idle must still refuse if a recording started before Run was pressed.
        if (_engineBusy?.Invoke() is string busy)
        {
            ShowStatus(busy, isError: false);
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _dispatch(() => IsRunning = true);
        try
        {
            string segModel = _resolveModel(DiarisationModels.Segmentation);
            string embModel = _resolveModel(DiarisationModels.Embedding);

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
                // explicitCount ("Run with count") is the user typing an exact speaker count, so it
                // wins over both the declared-count force and Auto. Without this the typed count was
                // silently dropped and Auto ran instead (2026-07-30 fix). forceDeclaredCount remains
                // the count-mismatch panel's path; Auto (both null) leaves forced null.
                int? forced = explicitCount ?? (forceDeclaredCount ? source.DeclaredCount : null);
                // EmitEmbeddings (voiceprint design 2026-07-25): per-cluster mean speaker vectors
                // for matching + enrollment. Additive on the wire - an older helper simply omits
                // them and the whole feature degrades to "no suggestions".
                var request = new DiarisationRequest(
                    source.LegPath, source.Source, segModel, embModel, forced, EmitEmbeddings: true);
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

            // Matter-pool matching runs HERE, off the dispatch, so its file IO never touches the
            // UI thread - and so its answer is already in hand when the single publish turn below
            // runs (see the atomicity note there).
            var suggestions = await ComputeMatterPoolSuggestionsAsync(newResultBySource, ct);

            // Only now - after every selected source ran to completion - replace the VM's
            // committed state, together with the UI-facing Clusters/CountMismatch/CanForceCount,
            // inside the SAME dispatch turn (Task 8 re-review fix). _dispatch is Dispatcher.
            // BeginInvoke - fire-and-forget - so writing the fields outside this block would let
            // _assignmentBySource jump ahead of Clusters, opening a window where a concurrent
            // ConfirmAsync passes its guard against the new assignment but still reads the stale
            // Clusters, producing a commit whose Assignments reference clusterKeys absent from
            // Names. A fresh run fully replaces prior state (no merge with stale sources).
            // Voiceprint suggestions join that same turn, stamped onto each row BEFORE it is added
            // to Clusters: a row must never be observable without its chip (nor a chip without its
            // row), which a second dispatch turn would allow.
            _dispatch(() =>
            {
                _resultBySource = newResultBySource;
                _assignmentBySource = newAssignmentBySource;
                Clusters.Clear();
                foreach (var c in freshClusters)
                {
                    c.Suggestion = suggestions.GetValueOrDefault(c.ClusterKey);
                    Clusters.Add(c);
                }
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
        catch (DiarisationException ex) { ShowDiarisationError(ex); }
        catch (Exception ex) { ShowStatus("Diarisation failed: " + ex.Message, isError: true); }
        finally { _cts = null; _dispatch(() => IsRunning = false); }
    }

    /// <summary>The DEFAULT suggestion pass (voiceprint design 2026-07-25): match this run's
    /// cluster embeddings against the people linked from the session's matters' rosters. Runs OFF
    /// the dispatch; returns clusterKey -> suggestion. Every failure - no people.json, a corrupt
    /// or forward-versioned one, an unreadable matter - degrades to "no suggestions", never to an
    /// error surface and never to a blocked run: a suggestion is advisory, so silence always beats
    /// noise. Cancellation is NOT swallowed (it is not a matching failure): it propagates so
    /// RunAsync's existing cancel path still publishes nothing at all.</summary>
    private async Task<IReadOnlyDictionary<string, VoiceprintSuggestion>> ComputeMatterPoolSuggestionsAsync(
        IReadOnlyDictionary<SourceKind, DiarisationResult> results, CancellationToken ct)
    {
        if (results.Values.Any(r => r.ClusterEmbeddings is null))
            System.Diagnostics.Debug.WriteLine(
                "SplitSpeakers: diarisation returned no cluster embeddings - no voiceprint suggestions.");
        try { return MatchAgainst(results, await MatterPoolAsync(_matterIds, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Dictionary<string, VoiceprintSuggestion>();
        }
    }

    /// <summary>The DEFAULT suggestion pool: the people linked from the rosters of
    /// <paramref name="matterIds"/>. Shared by the post-run pass above and the hydrated pass below
    /// (design 2026-07-28 task 7) so there is exactly one definition of "who is a candidate";
    /// takes the matter ids as an argument rather than reading <c>_matterIds</c> because hydration
    /// matches BEFORE <see cref="Apply"/> has captured them. Empty pool = no suggestions (both
    /// <see cref="MatchAgainst"/> and VoiceprintMatcher.Suggest short-circuit on it).
    ///
    /// Final review finding I1: NOTHING in the product writes RosterMember.PersonId yet, so reading
    /// it alone made this pool permanently empty - the design's DEFAULT (matter-scoped) suggestion
    /// pass could never produce a chip, and only the opt-in global button was reachable.
    /// RosterPersonResolver keeps an explicit PersonId strictly ahead of everything and falls back
    /// to an exact-ordinal Person NAME match, which is what makes the matter pool reachable today.
    /// Still suggest-only: this is a candidate list, never an assignment.</summary>
    private async Task<IReadOnlyList<Person>> MatterPoolAsync(
        IReadOnlyList<string> matterIds, CancellationToken ct)
    {
        var registry = await _people.LoadAsync(ct);
        if (registry is null) return [];
        var matters = await _loadMatters(matterIds, ct);
        var linkedIds = RosterPersonResolver.PersonIds(matters.SelectMany(m => m.Roster), registry);
        return registry.People.Where(p => linkedIds.Contains(p.Id)).ToList();
    }

    /// <summary>The hydrated sibling of <see cref="ComputeMatterPoolSuggestionsAsync"/> (design
    /// 2026-07-28 task 7): match the vectors ALREADY on disk for this version, since a hydrated row
    /// has no DiarisationResult to take them from. No key composition, unlike
    /// <see cref="MatchAgainst"/> - embeddings.json is keyed by the FULL post-remap clusterKey
    /// ("Remote:0") and carries its own Method (ClusterEmbeddings.cs:3-7), whereas a run's
    /// DiarisationResult.ClusterEmbeddings is keyed by bare cluster id. The matcher is key-agnostic,
    /// so the persisted entries go in verbatim, which is also why a hydrated chip can never point at
    /// a key speakers.json does not name.
    ///
    /// Degrades to "no suggestions" on every failure, exactly like the post-run pass: chips are
    /// advisory (:73-76) and must never block or fail a dialog whose whole point is renaming.
    /// Cancellation still propagates - it is not a matching failure, and LoadAsync must publish
    /// nothing at all when its token is signalled.</summary>
    private async Task<IReadOnlyDictionary<string, VoiceprintSuggestion>> ComputeHydratedSuggestionsAsync(
        ClusterEmbeddings? persisted, IReadOnlyList<string> matterIds, CancellationToken ct)
    {
        var none = new Dictionary<string, VoiceprintSuggestion>(StringComparer.Ordinal);
        // No file, an empty one, or one with no Method: nothing comparable to match against. The
        // Method test matters - VoiceprintMatcher only scores enrollments recorded under the SAME
        // method (VoiceprintMatcher.cs:32-34), so a blank one can only ever produce silence.
        if (persisted is null || persisted.Entries.Count == 0 || string.IsNullOrEmpty(persisted.Method))
            return none;
        try { return VoiceprintMatcher.Suggest(persisted.Entries, persisted.Method, await MatterPoolAsync(matterIds, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return none; }
    }

    // Pure fan-out over the run's per-source results. DiarisationResult.ClusterEmbeddings is keyed
    // by BARE cluster id ("0"); the matcher is key-agnostic, so the full "{Source}:{id}" clusterKey
    // is composed here - the same key the rows, the commit, and embeddings.json use.
    private static IReadOnlyDictionary<string, VoiceprintSuggestion> MatchAgainst(
        IReadOnlyDictionary<SourceKind, DiarisationResult> results, IReadOnlyList<Person> pool)
    {
        var all = new Dictionary<string, VoiceprintSuggestion>(StringComparer.Ordinal);
        if (pool.Count == 0) return all;
        foreach (var (source, result) in results)
        {
            if (result.ClusterEmbeddings is null || result.EmbeddingMethod is null) continue;
            var keyed = result.ClusterEmbeddings.ToDictionary(
                kv => $"{source}:{kv.Key}", kv => kv.Value, StringComparer.Ordinal);
            foreach (var (k, s) in VoiceprintMatcher.Suggest(keyed, result.EmbeddingMethod, pool))
                all[k] = s;
        }
        return all;
    }

    // Explicit, user-invoked global pass. Unlike the default pass this one DOES surface a failure:
    // the user asked for it and deserves to know it produced nothing because people.json could not
    // be read (design: corrupt people.json reports - it is user data, not derived).
    private async Task SearchAllPeopleAsync()
    {
        ClearStatus();
        // Snapshot before the first await, on the dispatch thread: a Run pass replaces the field
        // wholesale, and this search must not straddle two different runs' results.
        var results = _resultBySource;
        var hydrated = _hydratedEmbeddings;
        try
        {
            var registry = await _people.LoadAsync(CancellationToken.None);
            if (registry is null) return;
            // Hydrated rows have no run to match against (design 2026-07-28 task 7) - their vectors
            // are the ones already in embeddings.json, keyed by the FULL clusterKey, so the matcher
            // takes them verbatim with no key composition. Without this branch the button would be
            // ENABLED on a hydrated dialog (CanSearchAllPeople only asks for Clusters.Count > 0)
            // and silently do nothing, because MatchAgainst would be fanning out over an empty
            // _resultBySource. A non-empty _resultBySource always wins: it means a run republished
            // these rows, and cluster ids restart at 0 per run, so the persisted entries would then
            // be describing different voices than the rows on screen.
            var all = results.Count > 0 || hydrated is null
                ? MatchAgainst(results, registry.People)
                : VoiceprintMatcher.Suggest(hydrated.Entries, hydrated.Method, registry.People);
            _dispatch(() =>
            {
                // Final review finding I3: `results` was snapshotted on the dispatch thread, but
                // this turn runs later and iterates the LIVE Clusters. CanSearchAllPeople blocks a
                // search during a run but NOT a run during a search, so a Run publish landing
                // inside the await window above would leave run-1 vectors deciding a chip on a
                // run-2 row carrying the same "Remote:0" key (ids restart at 0 every run - THE
                // REMAP RULE) - i.e. a chip naming a person who is not that voice, which
                // suggest-only forbids. The publish swaps _resultBySource inside its own turn, so
                // this identity check is exactly "are these still the rows my answer was about?".
                // It covers the hydrated branch above too: _hydratedEmbeddings is written only by
                // Apply, in the same turn that installs a fresh _resultBySource instance, so a
                // reload that swapped the rows out always swaps this reference as well.
                if (!ReferenceEquals(_resultBySource, results)) return;
                // Never overwrite what the user already decided: an accepted row keeps its person,
                // and a row the user typed a name into is no longer looking for an identity.
                foreach (var row in Clusters)
                    if (row.AcceptedPersonId is null && row.IsDefaultNamed &&
                        all.TryGetValue(row.ClusterKey, out var s))
                        row.Suggestion = s;
            });
        }
        // Task 12 review fix (Finding 1): name the action that actually failed - "the split
        // failed" would be wrong when only the opt-in global search did.
        catch (Exception ex)
        { ShowStatus("Search all people failed: " + ex.Message, isError: true); }
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
        ClearStatus();
        var selected = Sources.Where(s => s.Selected).ToList();
        if (selected.Count == 0)
        {
            // CanConfirm now blocks this for a real button press (fix round 1, I2), but say it
            // anyway: AsyncRelayCommand.ExecuteAsync bypasses CanExecute entirely, and a silent
            // return is what made this state so easy to miss in the first place.
            ShowStatus("Select at least one audio source before confirming.", isError: false);
            return;
        }
        // Precondition (Task 8 review fix): every selected source must have a completed run
        // recorded in _assignmentBySource. Without this, a selected-but-never-run source (or one
        // whose run was superseded by a later cancelled/failed pass) would sail through with an
        // empty assignment/method - persisting an incomplete "diarised" commit into speakers.json.
        if (selected.Any(s => !_assignmentBySource.ContainsKey(s.Source)))
        {
            ShowStatus("Run diarisation for all selected sources before confirming.", isError: false);
            return;
        }
        try
        {
            var sources = selected.Select(s => s.Source).ToList();
            var assignments = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            // Deliberately keyed to the SELECTED sources only, never to everything _resultBySource
            // holds: a source that RAN and was then deselected is not part of this commit, and its
            // raw (un-remapped) keys must never reach embeddings.json.
            var resultsForCommit = new Dictionary<string, DiarisationResult>(StringComparer.Ordinal);
            string method = "";
            foreach (var s in selected)
            {
                if (!_assignmentBySource.TryGetValue(s.Source, out var assignment)) continue;
                assignments[s.Source.ToString()] = assignment.SeqToClusterKey;
                if (_resultBySource.TryGetValue(s.Source, out var result))
                {
                    method = result.Method;
                    resultsForCommit[s.Source.ToString()] = result;
                }
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

            // Accepted-suggestion provenance (voiceprint design 2026-07-25): what the user actually
            // accepted, and nothing else. A row whose name was edited after accepting has already
            // cleared its AcceptedPersonId, so it contributes nothing here. Raw (pre-remap) keys:
            // SpeakersMerge remaps Provenance exactly like Names.
            //
            // Both this map and the enrollment intents cover ONLY the committed sources. A source
            // that RAN and was then deselected keeps its rows in Clusters, but this confirm
            // re-asserts nothing about it - its speakers.json names, its embeddings.json entries
            // and its clusterKeys are all left exactly as they were - so neither an accept event
            // nor a voiceprint may be recorded against it here.
            //
            // Both are also snapshotted HERE, before the save await: Clusters is an
            // ObservableCollection a concurrent Run pass republishes wholesale, and nothing may
            // read it off the dispatch thread.
            var committedSources = sources.ToHashSet();
            var provenance = new Dictionary<string, SuggestionProvenanceEntry>(StringComparer.Ordinal);
            var enrollmentIntents = new List<EnrollmentIntent>();
            foreach (var cluster in Clusters)
            {
                if (!committedSources.Contains(cluster.Source)) continue;
                if (cluster.AcceptedPersonId is not null && cluster.AcceptedScore is double score)
                    provenance[cluster.ClusterKey] =
                        new SuggestionProvenanceEntry(cluster.AcceptedPersonId, score, _time.GetUtcNow());
                enrollmentIntents.Add(new EnrollmentIntent(
                    cluster.ClusterKey, cluster.AcceptedPersonId, names[cluster.ClusterKey],
                    cluster.RememberVoice, cluster.IsDefaultNamed));
            }

            // Rename-only confirm (design 2026-07-28 task 7): NO selected source has a
            // DiarisationResult, i.e. every one of them was hydrated from disk rather than run in
            // this dialog, so there are no fresh segments, no fresh vectors and no new cluster ids
            // to commit - only names (and the ownership/provenance that ride with them).
            //
            // Routing this through SaveDiarisationAsync would be wrong, not merely wasteful:
            // SpeakersMerge treats a commit's keys as FRESH and remaps any that collide with a
            // pinned or participant-owned key (SpeakersMerge.cs:49-91) - and on a rename the
            // "fresh" keys ARE the existing keys, so a pinned cluster would collide with itself,
            // be relocated to an unused id, and end up duplicated across two rows. It would also
            // restamp Method/DiarisedAtUtc for a run that never happened and re-derive
            // embeddings.json from vectors nothing has.
            //
            // The predicate is all-or-nothing rather than per-source because a MIXED state is
            // unreachable by construction: RunAsync replaces _resultBySource AND
            // _assignmentBySource wholesale and rebuilds Clusters from its own results (:631-638),
            // so running one side of a two-side hydration DISCARDS the other side's hydrated
            // assignment - after which the precondition above refuses a confirm that still selects
            // it. A run that cancels or throws replaces neither, leaving the load's hydration
            // intact and this branch correctly selected. (Pinned by
            // SplitSpeakersHydrationTests.A_run_replaces_hydrated_state_wholesale_*.)
            bool renameOnly = selected.All(s => !_resultBySource.ContainsKey(s.Source));
            if (renameOnly)
            {
                // Scoped to the committed sources, because RenameSpeakersAsync applies no source
                // filter of its own - it renames any key the overlay already holds, by its
                // documented trust model. The fresh path gets that filter for free from
                // SpeakersMerge's reSources guard, which is what makes "a deselected source's
                // speakers.json names are left exactly as they were" (see the note at the
                // provenance block above) true there. Without this the two paths would disagree,
                // and worse: a deselected row's NAME would land while its provenance and
                // enrollment - already scoped - did not, leaving an accepted machine suggestion
                // indistinguishable from a hand-typed name (Speakers.cs:19-21 forbids exactly
                // that).
                var renameNames = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var cluster in Clusters)
                    if (committedSources.Contains(cluster.Source))
                        renameNames[cluster.ClusterKey] = names[cluster.ClusterKey];
                // owned is participantId -> clusterKey, so its keys carry no source; renameNames
                // holds exactly the committed sources' clusterKeys, which makes it the filter.
                var renameOwned = owned
                    .Where(kv => renameNames.ContainsKey(kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

                // renameOwned is passed ALWAYS, possibly EMPTY - never conditionally (fix round 1,
                // C1). An empty map is a real instruction: "these sources re-assert no ownership",
                // which is what makes RenameSpeakersAsync clear a stale participant ClusterKey the
                // user just renamed out from under. Only null would mean "leave meta alone", and a
                // rename that leaves a stale owner behind is a transcript rendering the OWNER's
                // name over the cluster - NameResolver ranks ownership ahead of speakers.Names.
                bool wrote = await _maintenance.RenameSpeakersAsync(
                    _sessionId, _versionId, sources, renameNames, renameOwned, provenance,
                    CancellationToken.None);

                // Unconditional, exactly as on the fresh path: enrollment is the user's own
                // confirm-time opt-in and must not ride on whether a NAME happened to change.
                // Ticking "Remember voice" on a row that is already correctly named is a consent
                // act in its own right, and gating it on `wrote` would silently drop it. No remap
                // is passed because nothing was re-keyed: these clusterKeys are the ones already in
                // speakers.json, which is also how embeddings.json is keyed.
                bool enrolled = await EnrollConfirmedVoicesAsync(
                    enrollmentIntents, new Dictionary<string, string>(StringComparer.Ordinal));

                // Gated, unlike the fresh path: RenameSpeakersAsync returns false when it wrote
                // nothing at all, and a no-op confirm has no projections or grid state to refresh.
                // But it must not be SILENT either (fix round 1, I3) - without this the dialog just
                // sits there after a Confirm that legitimately had nothing to do. Worded about the
                // NAMES only, because the enrollment above runs either way and may well have saved
                // a voiceprint on this very pass.
                // The acks are gated on `enrolled` (review fix): a failed enrollment has already
                // shown its own error in the single status slot, and a trailing success ack would
                // overwrite it - a false positive worse than the hidden feedback this fixes.
                if (wrote)
                {
                    _dispatch(() => DiarisationSaved?.Invoke(_sessionId));
                    if (enrolled) ShowStatus("Speaker names saved.", isError: false);
                }
                else if (enrolled) ShowStatus("Speaker names were already up to date.", isError: false);
                return;
            }

            var commit = new DiarisationCommit(sources, assignments, names, method, _time.GetUtcNow(), provenance);
            var remap = await _maintenance.SaveDiarisationAsync(
                _sessionId, commit, _versionId, owned, resultsForCommit, CancellationToken.None);

            bool freshEnrolled = await EnrollConfirmedVoicesAsync(enrollmentIntents, remap);

            // Stage 5.4 C2 Task 3: only reached when the persist completed without throwing.
            // Ack gated on the enrollment outcome (review fix): its failure status must survive.
            _dispatch(() => DiarisationSaved?.Invoke(_sessionId));
            if (freshEnrolled) ShowStatus("Speaker split saved.", isError: false);
        }
        catch (Exception ex)
        { ShowStatus("Couldn't save the speaker split: " + ex.Message, isError: true); }
    }

    /// <summary>One row's confirm-time enrollment inputs, captured on the dispatch thread before
    /// the save await so the enrollment pass never touches the live Clusters collection.</summary>
    private sealed record EnrollmentIntent(
        string ClusterKey, string? AcceptedPersonId, string EffectiveName,
        bool RememberVoice, bool IsDefaultNamed);

    /// <summary>Confirm-time voiceprint enrollment (voiceprint design 2026-07-25). The confirm IS
    /// the consent gate - nothing enrolls without the user pressing it. Exactly ONE request per
    /// row, by priority: an accepted suggestion, else an effective name that matches a
    /// person-LINKED roster member of the session's matters, else a ticked "Remember voice" (which
    /// creates/uses a person by the typed name, and is ignored for a row still carrying its default
    /// label - a "Remote Speaker 2" voiceprint identifies nobody).
    ///
    /// THE REMAP RULE: every clusterKey is translated through the merge's
    /// <c>FreshKeyRemap</c> first. Cluster ids restart at 0 each run, so a fresh key colliding with
    /// a pinned/owned one was just relocated by SpeakersMerge, and embeddings.json was written
    /// under the POST-remap keys. Enrolling the raw key would at best find no vector and at worst
    /// - if that key survives from an earlier run - enroll a DIFFERENT human's voice.
    ///
    /// Never blocks the confirm: the diarisation is already durably saved by the time this runs, so
    /// a failure here is reported (people.json is user data - the design calls for reporting a
    /// corrupt one) and the DiarisationSaved event still fires.</summary>
    /// <returns>False when enrollment failed - the status slot already carries its error, and the
    /// caller must NOT overwrite it with a success acknowledgment (review fix). True otherwise,
    /// including "nothing to enroll".</returns>
    private async Task<bool> EnrollConfirmedVoicesAsync(
        IReadOnlyList<EnrollmentIntent> intents, IReadOnlyDictionary<string, string> remap)
    {
        if (intents.Count == 0) return true;
        try
        {
            var rosterPersonByName = await RosterPersonLinksAsync();
            var requests = new List<ClusterEnrollmentRequest>();
            foreach (var intent in intents)
            {
                string key = remap.TryGetValue(intent.ClusterKey, out var remapped)
                    ? remapped : intent.ClusterKey;

                // Exactly one request per row: the first rule that matches wins.
                if (intent.AcceptedPersonId is string acceptedId)
                    requests.Add(new ClusterEnrollmentRequest(key, acceptedId, null));
                else if (rosterPersonByName.TryGetValue(intent.EffectiveName, out var linkedId))
                    requests.Add(new ClusterEnrollmentRequest(key, linkedId, null));
                else if (intent.RememberVoice && !intent.IsDefaultNamed)
                    requests.Add(new ClusterEnrollmentRequest(key, null, intent.EffectiveName));
            }
            if (requests.Count > 0)
                await _enrollment.EnrollFromConfirmAsync(
                    _sessionId, _versionId, requests, CancellationToken.None);
            return true;
        }
        // Task 12 review fix (Finding 1): by the time this runs, ConfirmAsync's
        // SaveDiarisationAsync has already durably persisted the split. A user whose people.json
        // is corrupt must not read this as "the split failed" - the reused "Split speakers" title
        // said exactly that. Say what actually failed and what survived instead.
        catch (Exception ex)
        {
            ShowStatus("Voiceprints could not be saved. The speaker split was saved. ("
                       + ex.Message + ")", isError: true);
            return false;
        }
    }

    // name -> PersonId for every person-LINKED roster member of the session's matters (first link
    // wins). Advisory: an unreadable matter degrades to "no links", never to a failed confirm.
    //
    // Final review finding I1: an explicit RosterMember.PersonId still wins, but nothing writes one
    // yet, so reading only that made enrollment rule 2 dead code. RosterPersonResolver adds the
    // exact-ordinal Person NAME fallback - the same rule VoiceprintEnrollmentService's backfill
    // uses - which is what makes rule 2 reachable today. Consent is unaffected: the user still has
    // to type/pick that name and press Confirm, and nothing here assigns a name.
    private async Task<IReadOnlyDictionary<string, string>> RosterPersonLinksAsync()
    {
        try
        {
            var registry = await _people.LoadAsync(CancellationToken.None);
            var matters = await _loadMatters(_matterIds, CancellationToken.None);
            return RosterPersonResolver.LinkByName(matters.SelectMany(m => m.Roster), registry);
        }
        catch (Exception) { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private void ShowDiarisationError(DiarisationException ex)
    {
        if (ex.Code == DiarisationErrorCode.ModelDownloadFailed)
        {
            ShowStatus("Diarisation models are missing. Run tools/fetch-models.ps1, or set " +
                       "LOCALSCRIBE_MODELS to a folder containing them.", isError: true);
            return;
        }
        ShowStatus("Diarisation failed: " + ex.Message, isError: true);
    }
}

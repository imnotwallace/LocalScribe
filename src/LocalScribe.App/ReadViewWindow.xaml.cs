using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
namespace LocalScribe.App;

/// <summary>Formats DisplayRow.StartMs per the settings snapshot the VM loaded with, using
/// the canonical TimestampFormat (same stamps as the file renders). The window assigns Vm
/// before rows render (LoadAsync completes before Rows populate).</summary>
public sealed class ReadViewStampConverter : IValueConverter
{
    public ReadViewViewModel? Vm { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Vm is not null && value is long ms
            ? TimestampFormat.Stamp(ms, Vm.TimestampsMode, Vm.StartedAtLocal)
            : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>One instance per opened session (design section 2/5). Genuinely closes (nothing
/// depends on it - unlike the live view's hide-on-close). Registered in WindowRegistry so
/// session delete can close read views first and release audio handles. Capture-excluded by
/// default per settings.Privacy (design section 2) via the shared CaptureExclusion.Apply helper
/// (Task 13). Placement: "readViewDefault" written by the LAST closed read view; new windows
/// cascade +24px per already-open read view, screen-clamped.</summary>
public partial class ReadViewWindow
{
    private readonly ReadViewViewModel _vm;
    private readonly string _sessionId;
    private readonly WindowRegistry _registry;
    private readonly WindowStateStore _stateStore;
    private readonly ISettingsService _settings;
    private readonly Action<string> _openSplitSpeakers;
    private readonly Action<string> _openSessionDetails;
    // Export... header button (design 2026-08-03 section 10): the SAME hoisted factory the
    // Sessions page and (via TrayIconHost) the Record console close over - App.xaml.cs constructs
    // one ExportDialogViewModel/ExportDialog per click, never cached here.
    private readonly Action<string, string> _openExport;
    private readonly int _openAtCreation;
    // Tier 1B (2026-08-05, T1-3): set by ConfirmCloseAsync (Save-clean or Discard) so the
    // re-entrant Close() it issues skips the prompt instead of looping. Same field, same purpose
    // and same one-line comment as SessionDetailsWindow.xaml.cs:30, the guard this is ported from.
    private bool _closeConfirmed;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _hwndReady;

    /// <summary>Panel state for THIS window; XAML binds via ElementName=Self (an ObservableObject,
    /// so path updates propagate). Constructed by the openReadView composition (Task 6).</summary>
    public AssistantSidePanelViewModel Panel { get; }
    /// <summary>True once the user has clicked the Ask toggle in ANY read view this app run OR a
    /// persisted assistantPanel entry already existed: only then does OnClosed write the state.
    /// Before any explicit choice the heuristic must keep deciding (addendum precedence rule).
    /// STATIC and run-scoped (not per-instance): the approved design makes an explicit choice win
    /// for the whole read-view window family, not just the window it was made in. If it were an
    /// instance field, a choice made in window A would be lost when A closes before B (only the
    /// LAST closed read view ever saves - see the OpenCount==0 guard in OnClosed) - B's own instance
    /// flag would still be false and nothing would persist. A static survives A's close and is still
    /// true when B becomes the last-closed writer. Never reset it: it must live for the app run.</summary>
    private static bool s_panelChoiceIsExplicit;
    private const string PanelKey = "readView";
    private const double PanelDefaultWidth = 400;
    private const double PanelMinWidth = 280;
    private double _panelWidth = PanelDefaultWidth;

    // Stage 6.1 row context-menu commands. The menu is declared in a Style.Setter, so Click=
    // handlers there are never wired by the XAML compiler; the menu items bind to these instead
    // (via WindowProxy). They live on the window - not the WPF-free VM - because each opens a
    // window-owned modal dialog (Owner = this).
    public IAsyncRelayCommand<ReadRow> CorrectTextCommand { get; }
    public IAsyncRelayCommand<ReadRow> ReassignSpeakerCommand { get; }
    public IAsyncRelayCommand<ReadRow> ReassignClusterCommand { get; }
    public IAsyncRelayCommand<ReadRow> RemovePinCommand { get; }

    /// <summary>ITEM 5: per-segment seek from a read-view inline. On the window (like the other
    /// WindowProxy commands) so the item template can reach it; forwards to the WPF-free VM. Takes
    /// the segment's absolute start ms (boxed long) the SegmentText behavior passes.</summary>
    public IRelayCommand<long> SeekSegmentCommand { get; }

    // Task 14: Edit-mode toggle commands. Bound from the header buttons (direct children of the
    // window, NOT inside a Style/DataTemplate) via {Binding <Command>, ElementName=Self} - simple
    // ElementName binding is safe there because the source is the named element itself (its
    // properties, not its DataContext), so it works even though the window's DataContext is the
    // VM. Close over the `vm` constructor parameter directly (not the _vm field, which is not yet
    // assigned at this point) - same footgun the three commands above are already built to avoid.
    // Item 2: EnterEdit/SaveEdits now bind to anchor-preserving window methods (see
    // EnterEditPreservingScroll / SaveEditsPreservingScrollAsync below); Cancel remains a VM passthrough.
    public IRelayCommand EnterEditCommand { get; }
    public IAsyncRelayCommand SaveEditsCommand { get; }
    public IRelayCommand CancelEditCommand { get; }

    // Find-bar commands (design 2026-07-13 section 2.2 surface 3). Direct header/bar children bind
    // them via ElementName=Self; like the Edit commands they close over the `vm` ctor PARAMETER,
    // not the not-yet-assigned _vm field.
    public IRelayCommand OpenFindCommand { get; }
    public IRelayCommand FindNextCommand { get; }
    public IRelayCommand FindPreviousCommand { get; }
    public IRelayCommand CloseFindCommand { get; }
    public IRelayCommand SearchAllSessionsCommand { get; }

    public ReadViewWindow(ReadViewViewModel vm, string sessionId, WindowRegistry registry,
        WindowStateStore stateStore, ISettingsService settings, Action<string> openSplitSpeakers,
        Action<string> openSessionDetails, Action<string, string> openExport,
        AssistantSidePanelViewModel panelVm)
    {
        // Assigned BEFORE InitializeComponent: the XAML ElementName=Self bindings (Panel.IsOpen,
        // Panel.Summary, ...) resolve at InitializeComponent time and Panel must be non-null then.
        Panel = panelVm;
        CorrectTextCommand = new AsyncRelayCommand<ReadRow>(CorrectTextAsync);
        ReassignSpeakerCommand = new AsyncRelayCommand<ReadRow>(ReassignSpeakerAsync);
        ReassignClusterCommand = new AsyncRelayCommand<ReadRow>(ReassignClusterAsync);
        RemovePinCommand = new AsyncRelayCommand<ReadRow>(RemovePinAsync);
        SeekSegmentCommand = new RelayCommand<long>(vm.SeekSegment);
        // Item 2 (UX round 2026-08-02): Edit and Save route through the window's anchor-preserving
        // wrappers (instance methods are safe here - they run at click time, long after _vm is
        // assigned; only IMMEDIATE ctor-time invocation needs the `vm` parameter). Cancel stays a
        // bare passthrough: it does not rebuild Rows and RowList's own offset survives the
        // visibility swap (verified by runbook H3, per the spec's verify-first decision).
        EnterEditCommand = new RelayCommand(EnterEditPreservingScroll);
        SaveEditsCommand = new AsyncRelayCommand(SaveEditsPreservingScrollAsync);
        CancelEditCommand = new RelayCommand(vm.CancelEdit);
        OpenFindCommand = new RelayCommand(() => vm.OpenFind());
        FindNextCommand = new RelayCommand(vm.FindNext);
        FindPreviousCommand = new RelayCommand(vm.FindPrevious);
        CloseFindCommand = new RelayCommand(vm.CloseFind);
        SearchAllSessionsCommand = new RelayCommand(vm.RequestSearchAllSessions);
        InitializeComponent();
        // Review fix 2026-08-03 (CRITICAL): paste-only sanitisation for the go-to box, distinct
        // from the VM's per-keystroke TimestampMask.Format - see OnGoToBoxPaste. Attached to
        // GoToBox itself (a child of this same window), so - like the XAML-wired
        // PreviewKeyDown/TextChanged handlers on the same box - it needs no OnClosed teardown:
        // it does not reference anything that outlives the window.
        DataObject.AddPastingHandler(GoToBox, OnGoToBoxPaste);
        (_vm, _sessionId, _registry, _stateStore, _settings, _openSplitSpeakers, _openSessionDetails, _openExport)
            = (vm, sessionId, registry, stateStore, settings, openSplitSpeakers, openSessionDetails, openExport);
        DataContext = vm;
        ((ReadViewStampConverter)Resources["Stamp"]).Vm = vm;
        // Point the menu's binding proxy at this window so Data.<Command> resolves to the commands
        // above (the window's own DataContext is the VM, hence the explicit assignment).
        ((BindingProxy)Resources["WindowProxy"]).Data = this;
        // Per-session window that genuinely closes - OnClosed MUST unsubscribe (house rule).
        Panel.PropertyChanged += OnPanelPropertyChanged;
        _openAtCreation = registry.OpenCount;                        // count BEFORE registering this window
        registry.Register(sessionId, Close);
        // Re-apply capture exclusion when Privacy.ExcludeWindowsFromCapture is toggled while this
        // read view is open (design 2 + 6.2: applies immediately), mirroring Main/LiveViewWindow.
        // This is a per-session window that genuinely closes, so OnClosed MUST unsubscribe.
        _settings.Changed += OnSettingsChanged;
        // Task 17 live roster sync (design section 4): a Session Details save for THIS session
        // refreshes the speaker-choice lists without a reopen. Same per-session-window lifecycle
        // as the settings subscription above - OnClosed MUST unsubscribe.
        _registry.RosterChanged += OnRosterChanged;
        // IsAvailable is published on a later dispatcher turn inside Apply (via _dispatch =
        // Dispatcher.BeginInvoke), so the post-await read below can race it. Subscribing here
        // makes the timer start the moment IsAvailable flips true, whichever order wins.
        _vm.Playback.PropertyChanged += OnPlaybackPropertyChanged;
        // Find bar: focus the box when it opens; auto-scroll the read list to the current match.
        // Per-session window that genuinely closes - OnClosed MUST unsubscribe (house rule).
        _vm.PropertyChanged += OnVmPropertyChanged;
        // Item 8 one-shot go-to scroll. Per-session window that genuinely closes - OnClosed
        // MUST unsubscribe (house rule).
        _vm.GoToRowScrollRequested += OnGoToRowScrollRequested;
        // Item 1 jump-in realization; same per-session lifecycle - OnClosed MUST unsubscribe.
        _vm.EditFindJumpRequested += OnEditFindJump;
        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync(_sessionId, CancellationToken.None);
            if (_vm.Playback.IsAvailable && !_tick.IsEnabled) _tick.Start(); // fast path if already published
            if (_pendingFindTarget is { } t)                 // search-page click landed before load
            {
                ApplyFindTarget(t.Seq, t.Term);
                _pendingFindTarget = null;
            }
            await Panel.LoadAsync(_sessionId, CancellationToken.None);
            var savedPanel = _stateStore.LoadAssistantPanel(PanelKey);
            s_panelChoiceIsExplicit |= savedPanel is not null;
            ApplyPanelWidth(savedPanel?.Width ?? PanelDefaultWidth);
            // Explicit persisted choice wins; the heuristic (open iff the scope already has a
            // summary or chat history) applies only while no choice was ever recorded.
            Panel.IsOpen = savedPanel?.Open
                ?? (Panel.Summary?.HasSummary == true || Panel.Threads.HasAnyHistory);
            if (_pendingSummaryRegenerate is { } regen)
            {
                ApplySummaryAction(regen);
                _pendingSummaryRegenerate = null;
            }
        };
        _tick.Tick += (_, _) =>
        {
            _vm.TickPlayback();
            NudgeFollowIfNeeded();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);

        var saved = _stateStore.Load("readViewDefault");
        var p = ReadViewPlacement.Next(saved, _openAtCreation, Width, Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (p.X, p.Y);
        if (p.Width is double w) Width = w;
        if (p.Height is double h) Height = h;
    }

    // ISettingsService.Changed carries no thread contract; marshal to the UI thread before
    // touching the HWND. _hwndReady guards a save landing before the window was first shown.
    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
    {
        if (!CaptureExclusionPolicy.ShouldReapply(oldSettings, newSettings)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwndReady)
                CaptureExclusion.Apply(this, newSettings.Privacy.ExcludeWindowsFromCapture);
        });
    }

    // Idempotent: the ctor subscription and the post-await fast path in the Loaded handler above
    // both race to start _tick, whichever order Apply publishes IsAvailable in; the IsEnabled guard
    // ensures the timer starts exactly once.
    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackViewModel.IsAvailable)
            && _vm.Playback.IsAvailable && !_tick.IsEnabled)
            _tick.Start();
        // Item 7: enabling the toggle snaps to the current row immediately (spec decision);
        // disabling does nothing. ScrollRowToUpperThird range-guards the -1 sentinel itself.
        else if (e.PropertyName == nameof(PlaybackViewModel.SyncTranscript)
            && _vm.Playback.SyncTranscript && !_vm.IsEditMode)
            ScrollRowToUpperThird(_vm.PlayingSectionIndex);
    }

    private void ApplyPanelWidth(double width)
        => _panelWidth = Math.Max(PanelMinWidth, Math.Min(width, ActualWidth * 0.6));

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantSidePanelViewModel.IsOpen)) return;
        if (Panel.IsOpen)
        {
            PanelColumn.Width = new GridLength(_panelWidth);
            PanelColumn.MinWidth = PanelMinWidth;
            PanelColumn.MaxWidth = Math.Max(PanelMinWidth, ActualWidth * 0.6);
        }
        else
        {
            if (PanelColumn.Width.Value > 0) _panelWidth = PanelColumn.Width.Value;
            PanelColumn.MinWidth = 0;
            PanelColumn.Width = new GridLength(0);
        }
    }

    /// <summary>An actual user click on the Ask toggle (not the heuristic) makes the choice
    /// explicit - from now on it persists and the heuristic stops deciding. Family-scoped static:
    /// this wins for every read view for the rest of the app run, not just this window.</summary>
    private void OnAskToggleClick(object sender, RoutedEventArgs e) => s_panelChoiceIsExplicit = true;

    private void OnSplitSpeakers(object sender, RoutedEventArgs e) => _openSplitSpeakers(_sessionId);

    // Task 17 live roster sync: WindowRegistry.RosterChanged carries no thread contract (it fires
    // straight from Session Details' save continuation), so marshal to the UI thread before
    // touching the VM - mirrors OnSettingsChanged's Dispatcher.BeginInvoke pattern above. Only a
    // matching session id triggers a refresh; RefreshRosterAsync itself decides read-mode full
    // reload vs edit-mode choice-list-only refresh.
    private void OnRosterChanged(string sessionId)
    {
        if (sessionId != _sessionId) return;
        // Fire-and-forget, dispatched: same "_ = " discard style as the RefreshRowAsync calls in
        // App.xaml.cs - RefreshRosterAsync reports its own faults, so nothing is lost by not
        // awaiting here.
        Dispatcher.BeginInvoke(() => _ = _vm.RefreshRosterAsync(CancellationToken.None));
    }

    // Click-to-jump (design 4.1 Task 7): double-clicking a transcript section seeks playback to
    // its start and resumes there; the highlight follows via TickPlayback's PlayingSectionIndex.
    private void OnRowActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowList.SelectedIndex >= 0) _vm.JumpToSection(RowList.SelectedIndex);
    }

    // ---- Task 14: Edit-mode table ------------------------------------------------------------

    /// <summary>Collapsed edit row click -> expand into segments. TimestampsMode/StartedAtLocal
    /// are window/VM-level snapshot state (not per-section), which is why this is a code-behind
    /// handler rather than a pure ICommand: BeginEdit needs both passed in explicitly. Task 15 adds
    /// the two per-source SpeakerChoice lists here too, so each materialized segment can pick its
    /// own Source's candidates (BeginEdit hands each segment ChoicesFor(segment.Source)).</summary>
    private void OnEditRowActivated(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EditableSectionViewModel section)
            section.BeginEdit(_vm.TimestampsMode, _vm.StartedAtLocal,
                _vm.SpeakerChoicesForSource(TranscriptSource.Remote),
                _vm.SpeakerChoicesForSource(TranscriptSource.Local),
                _vm.CurrentSpeakerFor);
    }

    /// <summary>Task 15: "Manage speakers..." header button, visible only in Edit mode. Reaches
    /// the same Session Details window the row context menu's Reassign-speaker dialog opens via
    /// this identical callback (see ReassignSpeakerAsync above).</summary>
    private void OnManageSpeakers(object sender, RoutedEventArgs e) => _openSessionDetails(_sessionId);

    /// <summary>Export from the transcript you are already reading (design 2026-08-03 section 10).
    /// Reuses the SAME dialog the Sessions page opens - the session is always finalised here, so
    /// there is no live-export handling on this path.</summary>
    private void OnExport(object sender, RoutedEventArgs e) => _openExport(_sessionId, _vm.Title);

    // ---- Ctrl+F find bar (design 2026-07-13 section 2.2 surface 3) ----------------------------

    private (int Seq, string Term)? _pendingFindTarget;

    /// <summary>Ctrl+F opens the find bar; Ctrl+G focuses the go-to box (item 8). A window-level
    /// override rather than an InputBinding: KeyBindings sit outside the visual tree, where
    /// neither ElementName=Self nor the VM DataContext reliably resolves (the
    /// OnSegmentTextBoxPreviewKeyDown precedent).</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == System.Windows.Input.Key.F
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            _vm.OpenFind();
            e.Handled = true;
        }
        // Item 8: guarded on IsAvailable - the whole transport bar (and the box with it) is
        // collapsed when the session has no playable audio, and focusing a collapsed box no-ops
        // confusingly instead of doing nothing visibly.
        else if (e.Key == System.Windows.Input.Key.G
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
            && _vm.Playback.IsAvailable)
        {
            GoToBox.Focus();
            GoToBox.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Enter = next, Shift+Enter = previous, Esc = close (design 2.2). Code-behind on the
    /// box because it is a direct child (compiler-wired), unlike Style.Setter-nested elements.</summary>
    private void OnFindBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { _vm.CloseFind(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Shift)
        { _vm.FindPrevious(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        { _vm.FindNext(); e.Handled = true; }
    }

    /// <summary>Enter commits the jump; Esc returns focus to the transcript list (design item
    /// 8). Code-behind on the box for the same reason as OnFindBoxPreviewKeyDown: it is a
    /// direct child, so the XAML compiler wires the handler.</summary>
    private void OnGoToBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter
            && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
        {
            _vm.GoToTimestamp();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (_vm.IsEditMode) EditList.Focus(); else RowList.Focus();
            e.Handled = true;
        }
    }

    /// <summary>UX round 2026-08-03: the VM's auto-colon mask (TimestampMask, via
    /// OnGoToTextChanged) rewrites GoToText as the user types, and WPF resets
    /// TextBox.CaretIndex to 0 whenever Text is reassigned out from under the control - so
    /// without this, typing "1415" would land the caret before the "1" the instant the colon
    /// is inserted. The mask is left-anchored and append-only, so caret-at-end is correct for
    /// that append-typing flow, which is what this targets; it also fires (and still forces
    /// caret-to-end) for a mid-string edit such as backspacing in the middle or a paste that
    /// lands partway through existing text - those are rarer, and landing at the end there is a
    /// minor UX rough edge, not a correctness issue, since the box's full content is always
    /// re-derived from GoToText regardless of where the caret ends up.</summary>
    private void OnGoToBoxTextChanged(object sender, TextChangedEventArgs e)
        => GoToBox.CaretIndex = GoToBox.Text.Length;

    /// <summary>Sanitises a paste into the go-to box (review fix 2026-08-03, CRITICAL): unlike a
    /// keystroke, a paste is a single distinct event that can legitimately contain a genuine
    /// timestamp shape (a user's most likely paste source is copying a stamp straight out of the
    /// transcript) - so it goes through TimestampMask.Normalize, NOT the typing-path Format, to
    /// zero-pad short fields before the parser ever sees it. Without this, pasting a relative
    /// stamp like "1:02:03" (TimestampFormat.Stamp renders relative hours unpadded) would flatten
    /// through Format alone into "10:20:3" - a silently DIFFERENT time (10h20m3s vs 1h02m03s)
    /// that TimestampParser accepts without error. Replacing e.DataObject (rather than mutating
    /// the TextBox directly) lets WPF's normal paste-at-caret/replace-selection insertion still
    /// run, just with sanitised content; the VM's own Format pass then leaves a normalized paste
    /// untouched (it is a fixed point of Format - see TimestampMaskTests).</summary>
    private void OnGoToBoxPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text)) { e.CancelCommand(); return; }
        string normalized = TimestampMask.Normalize((string)e.DataObject.GetData(DataFormats.Text));
        var replacement = new DataObject();
        replacement.SetData(DataFormats.Text, normalized);
        e.DataObject = replacement;
    }

    /// <summary>Item 8 one-shot scroll for a committed go-to jump - deliberately NOT gated on
    /// the Sync toggle (spec: the jump scrolls "regardless of the Sync toggle"). The VM index is
    /// Rows-space (GoToTimestamp's SectionAt); the transport bar (and this box with it) stays
    /// visible while editing - it is gated on Playback.IsAvailable only, never IsEditMode - so a
    /// jump can land while EditList, not RowList, is the visible surface. Read mode reuses the
    /// follow scroll's centering + programmatic guard so the item-7 nudge cannot fight the
    /// settling scroll; edit mode remaps the index through FindScrollTargetForRow first, the same
    /// Rows-space-to-current-mode-space translation the find machinery (ApplyFindTarget) already
    /// does, then reuses its mode-aware ScrollIntoView helper.</summary>
    private void OnGoToRowScrollRequested(int index)
    {
        if (_vm.IsEditMode)
            ScrollFindTargetIntoView(_vm.FindScrollTargetForRow(index));
        else
            ScrollRowToUpperThird(index);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReadViewViewModel.IsFindOpen) && _vm.IsFindOpen)
            // The bar only just became visible - focus on the next dispatcher turn.
            Dispatcher.BeginInvoke(() => { FindBox.Focus(); FindBox.SelectAll(); });
        else if (e.PropertyName == nameof(ReadViewViewModel.CurrentFindRowIndex))
            ScrollFindTargetIntoView(_vm.CurrentFindRowIndex);
        // Item 7 follow: PlayingSectionIndex fires once per row ADVANCE (equality-gated), so
        // this scrolls once per section, never per 150 ms tick. -1 (before the first row /
        // after the media ends) never scrolls; edit mode never scrolls (read list collapsed).
        else if (e.PropertyName == nameof(ReadViewViewModel.PlayingSectionIndex)
            && _vm.Playback.SyncTranscript && !_vm.IsEditMode
            && _vm.PlayingSectionIndex >= 0 && _vm.PlayingSectionIndex < _vm.Rows.Count)
            ScrollRowToUpperThird(_vm.PlayingSectionIndex);
    }

    /// <summary>Mode-aware find scroll (item 1): the visible list is RowList in read mode and
    /// EditList in edit mode; the index is Rows-space or EditSections-space respectively (the
    /// VM's mode-space contract). Out-of-range indices (including -1) are ignored.</summary>
    private void ScrollFindTargetIntoView(int index)
    {
        if (_vm.IsEditMode)
        {
            if (index >= 0 && index < _vm.EditSections.Count)
                EditList.ScrollIntoView(_vm.EditSections[index]);
        }
        else if (index >= 0 && index < _vm.Rows.Count)
            RowList.ScrollIntoView(_vm.Rows[index]);
    }

    /// <summary>Item 1 jump-in: scroll + realize the target section so the FindSelection
    /// behavior's Loaded hook can apply the pending caret request - needed even when the current
    /// index did NOT change (a single match navigated twice raises no PropertyChanged).</summary>
    private void OnEditFindJump(int sectionIndex)
    {
        ScrollFindTargetIntoView(sectionIndex);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EditList.UpdateLayout());
    }

    /// <summary>Search-page click-through (design 2026-07-13 section 2.2): open the find bar on the
    /// clicked hit's term and scroll to the row containing the segment. Callable before the initial
    /// LoadAsync has finished - the target is stashed and applied right after load.</summary>
    public void ShowFindAt(int seq, string term)
    {
        if (!_vm.IsLoaded) { _pendingFindTarget = (seq, term); return; }
        ApplyFindTarget(seq, term);
    }

    private void ApplyFindTarget(int seq, string term)
    {
        _vm.OpenFind(term);
        int row = _vm.RowIndexOfSeq(seq);
        if (row < 0) return;
        _vm.MoveFindTo(row);
        // Scroll to the target row even when it is not itself a find match (an original-text-
        // only hit: the corrected text no longer contains the term, so the bar shows 0/0 -
        // truthful - but the reader still lands on the right segment). In edit mode the row maps
        // forward to its section (item 1 free rider: citations stop no-oping during edit).
        ScrollFindTargetIntoView(_vm.FindScrollTargetForRow(row));
    }

    private bool? _pendingSummaryRegenerate;

    /// <summary>Summary-column click-through (Phase 3/4): open the panel on this window - a
    /// PROGRAMMATIC open, so it never counts as the user's explicit choice - and optionally start
    /// a regeneration. Callable before the initial load; stashed and applied after (the
    /// ShowFindAt precedent).</summary>
    public void ShowAssistantSummary(bool regenerate)
    {
        if (!_vm.IsLoaded) { _pendingSummaryRegenerate = regenerate; return; }
        ApplySummaryAction(regenerate);
    }

    private void ApplySummaryAction(bool regenerate)
    {
        Panel.IsOpen = true;
        if (regenerate && Panel.Summary is { } summary && summary.RegenerateCommand.CanExecute(null))
            summary.RegenerateCommand.Execute(null);
    }

    /// <summary>Enter (no modifiers) in a segment's text box splits it at the caret (design §3.3).
    /// The owning section isn't reachable from the segment itself, so it's found by scanning
    /// EditSections for the one whose Segments contains this seg - EditSections is small (one
    /// entry per transcript section) so a linear scan is cheap. SplitSegment throws
    /// InvalidOperationException on a degenerate caret (would produce an empty half); that case is
    /// a deliberate no-op rather than a crash.</summary>
    private void OnSegmentTextBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter
            || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None)
            return;
        e.Handled = true;
        if (sender is not TextBox { DataContext: EditableSegmentViewModel seg } textBox) return;
        var section = _vm.EditSections.FirstOrDefault(s => s.Segments.Contains(seg));
        if (section is null) return;
        try { section.SplitSegment(seg, textBox.CaretIndex); }
        catch (InvalidOperationException) { /* degenerate caret: no-op, per brief */ }
    }

    /// <summary>"Merge" button on a split-child sub-row (design §3.3 revert/merge a split). Same
    /// owning-section lookup as OnSegmentTextBoxPreviewKeyDown above; RevertSplit takes the seq
    /// (not the individual part), so it restores the whole original segment regardless of which
    /// part's button was clicked.</summary>
    private void OnRevertSplit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EditableSegmentViewModel seg) return;
        var section = _vm.EditSections.FirstOrDefault(s => s.Segments.Contains(seg));
        if (section is null) return;
        section.RevertSplit(seg.Seq);
    }

    // ---- Stage 6.1: row context-menu editing -------------------------------------------------

    /// <summary>Marker rows have nothing to edit: suppress the menu outright rather than show
    /// three disabled items.</summary>
    private void OnRowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReadRow row && !row.HasSegments)
            e.Handled = true;
    }

    private async Task CorrectTextAsync(ReadRow? row)
    {
        if (row is null) return;
        var editor = _vm.CreateCorrectionEditor(_vm.Rows.IndexOf(row));
        if (editor is null) return;
        var dialog = new CorrectTextDialog(editor) { Owner = this };
        if (dialog.ShowDialog() == true) await ReloadPreservingScrollAsync();
    }

    private async Task ReassignSpeakerAsync(ReadRow? row)
    {
        if (row is null) return;
        var editor = _vm.CreateReassignEditor(_vm.Rows.IndexOf(row));
        if (editor is null) return;
        editor.OpenSessionDetailsRequested += _openSessionDetails;
        var dialog = new ReassignSpeakerDialog(editor) { Owner = this };
        if (dialog.ShowDialog() == true) await ReloadPreservingScrollAsync();
    }

    // Bulk cluster reassign (2026-07-30): seed the SAME reassign dialog with every segment of the
    // clicked row's detected cluster, so one confirm relabels all of "Local Speaker N". A null editor
    // means the row is an automatic Me/Them line with no cluster to gather - say so rather than no-op.
    private async Task ReassignClusterAsync(ReadRow? row)
    {
        if (row is null) return;
        var editor = _vm.CreateReassignClusterEditor(_vm.Rows.IndexOf(row));
        if (editor is null)
        {
            MessageBox.Show(this,
                "This line isn't attributed to a detected speaker, so there's nothing to reassign in bulk. Use \"Reassign speaker...\" for individual lines.",
                "Reassign all of this speaker", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        editor.OpenSessionDetailsRequested += _openSessionDetails;
        var dialog = new ReassignSpeakerDialog(editor) { Owner = this };
        if (dialog.ShowDialog() == true) await ReloadPreservingScrollAsync();
    }

    private async Task RemovePinAsync(ReadRow? row)
    {
        if (row is null) return;
        var confirmed = MessageBox.Show(this,
            "Remove the manual speaker pin(s) on this section? The label falls back to the automatic result; nothing else changes.",
            "Remove speaker pin", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        await _vm.RemovePinsAsync(_vm.Rows.IndexOf(row), CancellationToken.None);
        await ReloadPreservingScrollAsync();
    }

    /// <summary>Rows.Clear() resets the virtualized ListView's scroll position; capture and
    /// restore the vertical offset so an edit does not bounce the reader to the top. Restore is
    /// dispatched (layout must run once over the new rows before the offset is valid).</summary>
    private async Task ReloadPreservingScrollAsync()
    {
        var scroll = ScrollHelpers.FindScrollViewer(RowList);
        double offset = scroll?.VerticalOffset ?? 0;
        await _vm.ReloadRowsAsync(CancellationToken.None);
        if (scroll is not null)
            // Discard: DispatcherOperation is awaitable on this runtime, so a bare call in this
            // async method trips CS4014 (0-warning gate) - the restore is deliberately fire-and-
            // forget (same house style as the RefreshRowAsync fire-and-forgets in App.xaml.cs).
            _ = Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset));
    }

    /// <summary>Item 2 (UX round 2026-08-02): the realized item whose container is topmost in the
    /// list's viewport, plus its Y offset within the viewport. Realized containers only - a
    /// virtualized list has no containers for off-screen items, and the anchor is by definition
    /// on screen. Null when the template has not applied yet or nothing is visible.</summary>
    private static (object Item, double ViewportY)? TopVisibleItem(ListView list)
    {
        if (ScrollHelpers.FindScrollViewer(list) is not { } sv) return null;
        object? best = null;
        double bestY = double.MaxValue;
        foreach (var item in list.Items)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c
                || !c.IsVisible)
                continue;
            double y = c.TransformToAncestor(sv).Transform(default).Y;
            if (y + c.ActualHeight <= 0 || y >= sv.ViewportHeight) continue;   // outside viewport
            if (y < bestY) { bestY = y; best = item; }
        }
        return best is null ? null : (best, bestY);
    }

    /// <summary>Scrolls the list so item's container lands at the given viewport Y. ScrollIntoView
    /// alone only guarantees edge visibility, so a correction pass re-aligns to the captured Y -
    /// pixel scrolling (both lists set ScrollUnit=Pixel) makes offset math exact.</summary>
    private static void ScrollItemToViewportY(ListView list, object item, double viewportY)
    {
        list.ScrollIntoView(item);
        list.UpdateLayout();                                          // realize the container first
        if (ScrollHelpers.FindScrollViewer(list) is not { } sv) return;
        if (list.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement c) return;
        double y = c.TransformToAncestor(sv).Transform(default).Y;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + (y - viewportY));
    }

    /// <summary>Item 2: capture the topmost visible read row, enter Edit, then scroll its twin
    /// section to the same viewport Y. Deferred to Loaded priority WITH an explicit UpdateLayout:
    /// EditList was Collapsed until IsEditMode flipped, so it has never measured - a synchronous
    /// scroll would clamp to offset 0. Twin lookup is ReferenceEquals on the shared DisplayRow
    /// instance (the section wraps the very object the ReadRow holds; DisplayRow is a record, so
    /// == is value equality and could hit a lookalike row). A marker anchor falls FORWARD to the
    /// next non-marker row - markers have no edit section.</summary>
    private void EnterEditPreservingScroll()
    {
        // Ownership rule (2026-08-02 review fix): find-with-an-active-match owns transition
        // scrolling (its own RecomputeFindMatches -> CurrentFindRowIndex change ->
        // ScrollFindTargetIntoView runs synchronously inside EnterEditMode below); the anchor
        // owns it only when there is no active match to preserve instead. Checked at capture
        // time, before the transition, using the pre-transition (Rows-space) match state.
        bool findOwnsScroll = _vm.IsFindOpen && _vm.CurrentFindRowIndex >= 0;
        var anchor = findOwnsScroll ? null : TopVisibleItem(RowList);
        _vm.EnterEditMode();
        if (!_vm.IsEditMode || anchor is not { } a) return;   // gate refused, find owns scroll, or nothing visible
        int i = _vm.Rows.IndexOf((ReadRow)a.Item);
        while (i >= 0 && i < _vm.Rows.Count && _vm.Rows[i].Data.IsMarker) i++;
        if (i < 0 || i >= _vm.Rows.Count) return;
        var data = _vm.Rows[i].Data;
        var section = _vm.EditSections.FirstOrDefault(s => ReferenceEquals(s.Row, data));
        if (section is null) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            EditList.UpdateLayout();
            ScrollItemToViewportY(EditList, section, a.ViewportY);
        });
    }

    /// <summary>Item 2: Save rebuilds Rows wholesale (ReloadRowsAsync inside SaveEditsAsync), so
    /// the pre-save anchor is re-found BY VALUE - first segment Seq, then StartMs - never by
    /// reference. Mirrors ReloadPreservingScrollAsync's deferral (layout must run over the new
    /// rows before any offset math is valid). A failed save keeps IsEditMode true: scroll nothing,
    /// the user is exactly where they were.</summary>
    private async Task SaveEditsPreservingScrollAsync()
    {
        // Ownership rule (2026-08-02 review fix): find-with-an-active-match owns transition
        // scrolling (its own CurrentFindRowIndex -> ScrollFindTargetIntoView path runs inside
        // SaveEditsAsync's post-save recompute); the anchor owns it only when there is no active
        // match. Checked at capture time, before the save, using the pre-save (EditSections-
        // space) match state.
        bool findOwnsScroll = _vm.IsFindOpen && _vm.CurrentFindRowIndex >= 0;
        var anchor = findOwnsScroll ? null : TopVisibleItem(EditList);
        long anchorStart = -1;
        int anchorSeq = -1;
        double viewportY = 0;
        if (anchor is { } a && a.Item is EditableSectionViewModel s)
        {
            anchorStart = s.Row.StartMs;
            anchorSeq = s.Row.Segments.Count > 0 ? s.Row.Segments[0].Seq : -1;
            viewportY = a.ViewportY;
        }
        await _vm.SaveEditsAsync(CancellationToken.None);
        if (_vm.IsEditMode || anchorStart < 0) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            RowList.UpdateLayout();
            var target = (anchorSeq >= 0
                    ? _vm.Rows.FirstOrDefault(r => r.Data.Segments.Any(seg => seg.Seq == anchorSeq))
                    : null)
                ?? _vm.Rows.FirstOrDefault(r => !r.Data.IsMarker && r.Data.StartMs >= anchorStart);
            if (target is not null) ScrollItemToViewportY(RowList, target, viewportY);
        });
    }

    // Scrubbing guard (design 4.1 Task 4, revised Stage 5.4 smoke-fix): Playback.IsScrubbing
    // suppresses the position timer's Tick() - AND the TwoWay SliderValueMs binding's own commit
    // path - while the user is mid-drag, so neither can fight the thumb; DragCompleted commits the
    // final value via Seek() on release. Track-click and arrow/Page/Home/End keys never raise
    // Thumb.DragStarted/Completed (Slider's class handlers move Value directly), so those gestures
    // commit immediately through PlaybackViewModel.OnSliderValueMsChanged instead - there is
    // nothing left for a Preview*/KeyDown instance handler to do for them.
    private void OnSeekDragStarted(object sender, RoutedEventArgs e)
        => _vm.Playback.IsScrubbing = true;

    private void OnSeekDragCompleted(object sender, RoutedEventArgs e)
    {
        _vm.Playback.Seek(_vm.Playback.SliderValueMs);
        _vm.Playback.IsScrubbing = false;
    }

    // ---- Item 7 (UX round 2026-08-02): Sync-transcript follow scrolling -----------------------
    // UX round 2026-08-03 item A: the settling scroll is now an animated glide (ScrollGlide.cs)
    // instead of an instant ScrollToVerticalOffset snap, so the guard below spans the WHOLE
    // glide, not just one dispatcher turn - see its doc comment.

    /// <summary>True from the moment a follow/go-to scroll THIS window issued starts until its
    /// glide has FULLY settled - not just its first frame. ScrollIntoView, ScrollToVerticalOffset,
    /// and every intermediate frame of the glide all raise ScrollChanged, and the 150 ms nudge
    /// below could otherwise measure a mid-flight container (one that is still animating toward
    /// the upper third, not there yet) and re-scroll it on top of the glide every tick - so the
    /// flag is set before the scroll/glide begins and cleared only once the glide's onFinished
    /// runs, on a deferred dispatcher turn AFTER that final offset change has published its own
    /// ScrollChanged (mandatory per the spec's disengage design). That clearing closure is itself
    /// deferred and can therefore go stale - see _glideGeneration - so every place that schedules
    /// one stamps it with the generation current at schedule time and checks it before actually
    /// clearing the flag.</summary>
    private bool _programmaticFollowScroll;

    /// <summary>Bumped every time GlideTo (or ScrollRowToUpperThird's slow-path ScrollIntoView-only
    /// leg, before GlideTo has even run) takes over ownership of _programmaticFollowScroll. Every
    /// deferred release closure captures the generation that was current when IT was scheduled and
    /// only clears the guard if that generation is STILL current when the closure actually runs -
    /// otherwise it is a safe no-op. This closes a race that plain Cancel()/onFinished plumbing
    /// cannot: Cancel()'s own guard-release is itself deferred (Dispatcher.InvokeAsync at
    /// Background priority), so it can land AFTER a brand new glide has already started - e.g. the
    /// user drags the scrollbar mid-glide (DisengageSync calls _glide.Cancel(), which QUEUES the
    /// old glide's release), and before that queued release runs, the item-8 go-to jump - which is
    /// deliberately NOT gated on the Sync toggle - or a fresh enable-snap starts a new glide via
    /// GlideTo. Without the generation check, the stale queued release would clear the guard mid-
    /// flight of the NEW glide and let NudgeFollowIfNeeded re-trigger a scroll on top of it -
    /// exactly the stutter the guard exists to prevent, reached through a different door than the
    /// one ScrollGlide.Start's retarget path used to special-case (see ScrollGlide.Start's doc
    /// comment for why that special-casing was removed once this token made it unnecessary).</summary>
    private int _glideGeneration;

    /// <summary>The frame pump for the settling glide. One instance for the window's whole
    /// lifetime (not per-call): a new row advance while the previous glide is still airborne
    /// RETARGETS it via Start (see ScrollGlide.Start's doc comment) rather than fighting it with
    /// a second independent animation. Cancelled by DisengageSync (user grabs the scrollbar) and
    /// by OnClosed (window teardown) so no CompositionTarget.Rendering handler outlives either.</summary>
    private readonly ScrollGlide _glide = new();

    /// <summary>Shared by the item-7 follow, the enable-snap, and the item-8 go-to jump: bring
    /// Rows[index] into view, then glide it to ~1/3 from the viewport top.
    ///
    /// Fast path: the row's container is ALREADY realized (the normal row-advance case - only a
    /// handful of rows recycle in/out per tick under virtualization). Skip ScrollIntoView
    /// entirely here: it scrolls to the NEAREST viewport edge, which is exactly the first half of
    /// the double-jump this method used to produce (snap to an edge, then snap again to the upper
    /// third). With the container already in hand there is nothing ScrollIntoView would add -
    /// compute the target offset and glide straight to it from wherever the viewport already is.
    ///
    /// Slow path: the container is NOT realized (a big seek landed far from the last position, so
    /// the target row was virtualized away). There is no offset to glide FROM for a row that
    /// isn't laid out yet, so ScrollIntoView's edge-snap is unavoidable here - it exists solely to
    /// force realization. The subsequent glide on the deferred pass is the same centering leg as
    /// the fast path, just delayed until layout has run. If the container is STILL null after that
    /// (tolerated - the ScrollIntoView snap stands), release the guard directly, since nothing
    /// else will.
    ///
    /// Range-guards internally so the -1 sentinel never scrolls.</summary>
    private void ScrollRowToUpperThird(int index)
    {
        if (index < 0 || index >= _vm.Rows.Count) return;
        var scroll = ScrollHelpers.FindScrollViewer(RowList);
        if (scroll is not null
            && RowList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item)
        {
            GlideTo(scroll, TargetOffsetForUpperThird(scroll, item));
            return;
        }
        _programmaticFollowScroll = true;
        // Reserve a generation for this ScrollIntoView-only leg too, even though GlideTo has not
        // run yet: if a LATER call (a newer row advance's fast path, or another slow-path attempt)
        // takes over the guard before this leg's deferred pass below completes, the release
        // scheduled in the "still not realized" branch must be inert for the same reason GlideTo's
        // own releases are - see _glideGeneration's doc comment.
        int generation = ++_glideGeneration;
        RowList.ScrollIntoView(_vm.Rows[index]);
        _ = Dispatcher.InvokeAsync(() =>
        {
            // Bail before touching anything if a NEWER attempt has since taken over the guard
            // (a later row advance's fast path, or another slow-path attempt, or the item-8
            // go-to jump). Without this check the realized branch below would call GlideTo and
            // override that newer glide's destination with THIS stale one - the guard invariant
            // would still hold (GlideTo re-stamps its own generation), but the row it settles on
            // would be wrong. Returning here without releasing the guard is correct: a newer
            // attempt now owns it, and that attempt's own generation-gated release will clear it
            // in due course - the same induction that makes the stale releases below inert.
            if (generation != _glideGeneration) return;
            var settledScroll = ScrollHelpers.FindScrollViewer(RowList);
            if (settledScroll is not null
                && RowList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement settledItem)
            {
                GlideTo(settledScroll, TargetOffsetForUpperThird(settledScroll, settledItem));
            }
            else
            {
                // Still not realized (tolerated - the ScrollIntoView snap above stands): GlideTo
                // (which would normally own the release) never ran, so release the guard directly
                // on the same deferred-turn, generation-checked shape GlideTo itself uses.
                _ = Dispatcher.InvokeAsync(() =>
                {
                    if (generation == _glideGeneration) _programmaticFollowScroll = false;
                }, DispatcherPriority.Background);
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Upper-third target offset for `item` within `scroll`, clamped to the
    /// ScrollViewer's legal range [0, ExtentHeight - ViewportHeight]. Without the clamp, a row
    /// near either end of the list (first/last few rows) would compute an offset outside that
    /// range - ScrollToVerticalOffset silently clamps its OWN argument, so the glide's eased
    /// intermediate frames would animate toward a value the ScrollViewer never actually reaches,
    /// landing short of (or overshooting into a clamp on) the curve's real endpoint.</summary>
    private static double TargetOffsetForUpperThird(ScrollViewer scroll, FrameworkElement item)
    {
        double itemTop = item.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        double raw = scroll.VerticalOffset + itemTop - scroll.ViewportHeight / 3;
        return Math.Clamp(raw, 0, Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight));
    }

    /// <summary>Sets the programmatic-scroll guard and either glides to toOffset or, for users
    /// who have turned Windows animations off (SystemParameters.ClientAreaAnimation - the same
    /// system accessibility setting other apps respect), jumps instantly instead of animating a
    /// motion they explicitly opted out of. _glide.Cancel() runs FIRST, unconditionally, even on
    /// the instant-jump branch below (which has no pump of its own): without it, a glide started
    /// by a PRIOR call would keep calling ScrollToVerticalOffset every frame and fight this call's
    /// jump - e.g. the user turns Windows animations off mid-session while a glide is airborne.
    /// Either way the guard is released via the SAME deferred-clear shape: one dispatcher turn
    /// after the final offset change, stamped with THIS call's generation and checked against
    /// _glideGeneration before actually clearing the flag - see that field's doc comment for why
    /// a deferred release must be version-checked rather than trusted to still be current.</summary>
    private void GlideTo(ScrollViewer scroll, double toOffset)
    {
        _glide.Cancel();
        _programmaticFollowScroll = true;
        int generation = ++_glideGeneration;
        if (!SystemParameters.ClientAreaAnimation)
        {
            scroll.ScrollToVerticalOffset(toOffset);
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (generation == _glideGeneration) _programmaticFollowScroll = false;
            }, DispatcherPriority.Background);
            return;
        }
        _glide.Start(scroll, toOffset, () =>
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (generation == _glideGeneration) _programmaticFollowScroll = false;
            }, DispatcherPriority.Background));
    }

    /// <summary>Long-monologue nudge, driven by the same 150 ms timer as TickPlayback:
    /// PlayingSectionIndex only fires on a row ADVANCE, so once the playing row's container has
    /// left the viewport for any non-user reason (window resize, panel open/close, a reload's
    /// offset restore - which never re-fires PlayingSectionIndex) nothing else would bring it
    /// back. Skipped while a follow scroll is still settling. A container that exists and is
    /// even partially visible is left alone - no per-tick recentering churn.</summary>
    private void NudgeFollowIfNeeded()
    {
        if (!_vm.Playback.SyncTranscript || _vm.IsEditMode || _programmaticFollowScroll) return;
        int index = _vm.PlayingSectionIndex;
        if (index < 0 || index >= _vm.Rows.Count) return;
        var scroll = ScrollHelpers.FindScrollViewer(RowList);
        if (scroll is null) return;
        if (RowList.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item)
        {
            ScrollRowToUpperThird(index);            // virtualized away entirely: off-screen for sure
            return;
        }
        double top = item.TransformToAncestor(scroll).Transform(new Point(0, 0)).Y;
        if (top + item.ActualHeight < 0 || top > scroll.ViewportHeight)
            ScrollRowToUpperThird(index);
    }

    // Item 7 disengage: a real user scroll intent turns the follow toggle off. These three
    // gestures can ONLY originate from the user - programmatic ScrollIntoView /
    // ScrollToVerticalOffset raise ScrollChanged but never PreviewMouseWheel,
    // Thumb.DragStarted, or PreviewKeyDown - so the handlers need no guard-flag check; the
    // _programmaticFollowScroll flag protects the nudge path instead.
    private void OnRowListPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        => DisengageSync();

    private void OnRowListScrollThumbDragStarted(object sender, RoutedEventArgs e) => DisengageSync();

    private void OnRowListPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.PageUp or System.Windows.Input.Key.PageDown)
            DisengageSync();
    }

    private void DisengageSync()
    {
        // Stop the glide DEAD before flipping the toggle: without this, a user grabbing the
        // wheel/scrollbar/PageUp-Down mid-glide would have their own scroll fought by the
        // animation's next frame landing on top of it (Cancel's own doc comment covers why this
        // also safely releases the guard rather than stranding it).
        _glide.Cancel();
        if (_vm.Playback.SyncTranscript) _vm.Playback.SyncTranscript = false;
    }

    /// <summary>Close guard, ported from SessionDetailsWindow.xaml.cs:81-98 (Tier 1B design
    /// 2026-08-05, T1-3). Until now the ONLY editor in the product with no close protection was the
    /// one that edits evidence: a whole session's corrections, splits and re-attributions vanished
    /// on an X-click with no prompt.
    ///
    /// WPF cannot await inside OnClosing, so a dirty editor CANCELS this close and hands off to
    /// ConfirmCloseAsync, which shows the dialog and re-Closes (with _closeConfirmed set) only on
    /// Save-that-settled-clean or Discard.
    ///
    /// The focused-box force-commit stays HERE, BEFORE the dirty gate. In this window the edit
    /// TextBox already binds EditedText with UpdateSourceTrigger=PropertyChanged
    /// (ReadViewWindow.xaml:658), so today it is belt-and-braces - but the donor's rule is that a
    /// LostFocus-bound box never commits on an X-close, and committing AFTER the gate could drop a
    /// half-typed edit that is the only change. Any future LostFocus-bound field in this window is
    /// then covered by construction rather than by remembering to revisit this method.
    ///
    /// The donor's ParticipantRow branch is deliberately NOT ported - this window has no
    /// participant name boxes.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed) return;
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox tb)
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (!_vm.HasUnsavedEdits) return;       // clean: let the close proceed
        e.Cancel = true;                        // dirty: stop THIS close; decide via the async dialog
        _ = ConfirmCloseAsync();
    }

    /// <summary>Themed unsaved-changes prompt (WPF-UI 4.0.3 Wpf.Ui.Controls.MessageBox) - the donor's
    /// ConfirmCloseAsync with two deliberate substitutions.
    ///
    /// (1) It calls _vm.SaveEditsAsync DIRECTLY rather than the window's SaveEditsCommand: that
    /// command routes through SaveEditsPreservingScrollAsync, which captures a scroll anchor and
    /// re-scrolls the rebuilt list on a Dispatcher.BeginInvoke(DispatcherPriority.Loaded)
    /// continuation - pointless work on a window that is about to close, and a continuation queued
    /// against a closing window is exactly the kind of thing that throws later.
    ///
    /// (2) It re-reads HasUnsavedEdits instead of catching, because SaveEditsAsync NEVER throws: it
    /// catches, sets SaveError and returns with IsEditMode still true. A failed or partially-failed
    /// save therefore leaves the editor dirty and the window OPEN, with the in-window SaveError
    /// InfoBar already explaining why - the same semantics the donor gets from re-reading IsDirty.
    ///
    /// Secondary (Discard) reverts via CancelEdit and closes. None (Cancel / Esc / title-bar close)
    /// stays open. The dialog is shown on a user close action, long after the message pump is up, so
    /// the Wpf.Ui Mica-window-before-pump rendering gotcha does not apply.</summary>
    private async System.Threading.Tasks.Task ConfirmCloseAsync()
    {
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Owner = this,
            Title = "Unsaved changes",
            Content = "Save your transcript edits before closing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
        };
        switch (await dialog.ShowDialogAsync())
        {
            case Wpf.Ui.Controls.MessageBoxResult.Primary:      // Save
                await _vm.SaveEditsAsync(System.Threading.CancellationToken.None);
                if (_vm.HasUnsavedEdits) return;                // save failed - stay open, SaveError shows why
                _closeConfirmed = true;
                Close();
                break;
            case Wpf.Ui.Controls.MessageBoxResult.Secondary:    // Discard
                _vm.CancelEdit();                               // revert; also clears SaveError
                _closeConfirmed = true;
                Close();
                break;
            // MessageBoxResult.None (Cancel / Esc / title-bar close): keep editing - do nothing.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        // No CompositionTarget.Rendering handler may outlive the window - it is a static/app-wide
        // event, so a glide left running here would keep ticking (and keep this window's ScrollGlide,
        // and transitively this window, referenced) for the life of the app, not just this session.
        _glide.Cancel();
        // The settings service outlives this per-session window: unsubscribe or every opened-and-
        // closed read view would leak its predecessor through this Changed subscription.
        _settings.Changed -= OnSettingsChanged;
        _registry.RosterChanged -= OnRosterChanged;
        _vm.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.GoToRowScrollRequested -= OnGoToRowScrollRequested;
        _vm.EditFindJumpRequested -= OnEditFindJump;
        Panel.PropertyChanged -= OnPanelPropertyChanged;
        _vm.Dispose();                                               // releases both MediaPlayer file handles
        _registry.Unregister(_sessionId, Close);                     // remove ONLY this window's entry -
                                                                      // a Split-speakers dialog for the same
                                                                      // session id may still be open
        if (_registry.OpenCount == 0)                                // last closed read view writes the default
        {
            _stateStore.Save("readViewDefault", new WindowPlacement(Left, Top, Width, Height));
            if (s_panelChoiceIsExplicit)
                _stateStore.SaveAssistantPanel(PanelKey, new AssistantPanelState(Panel.IsOpen,
                    Panel.IsOpen ? PanelColumn.Width.Value : _panelWidth));
        }
        base.OnClosed(e);
    }
}

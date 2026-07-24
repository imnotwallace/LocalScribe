using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
namespace LocalScribe.App.Pages;

/// <summary>Humble shell over MattersPageViewModel: routes control events to VM commands.
/// The single delete confirmation dialog (design 4.1) is the only view-side decision here;
/// the referenced-block itself is VM logic via MatterDeleter.</summary>
public partial class MattersPage : Page
{
    private readonly MattersPageViewModel _vm;

    // ---- Phase 3: chat-only assistant panel (mirrors ReadViewWindow.xaml.cs's panel region -
    // see that file for the full precedent/rationale on each piece). Two differences drive the
    // extra machinery here: (1) this is a Page that StaticPageProvider REBUILDS on every
    // MainWindow open (not a window that is constructed once and genuinely closes), so the
    // lifecycle hooks are Loaded/Unloaded rather than the ctor/OnClosed; (2) the hosted Assistant
    // SWAPS per matter selection within a single page instance (RebuildAssistant), so the panel
    // subscription must be torn down and re-established on every swap, not just once.
    private const string PanelKey = "matters";
    private const double PanelDefaultWidth = 400;
    private const double PanelMinWidth = 280;
    private double _panelWidth = PanelDefaultWidth;

    /// <summary>True once the user has clicked the Ask toggle in ANY incarnation of this page
    /// this app run, OR a persisted assistantPanel entry already existed under key "matters".
    /// STATIC and run-scoped for the same reason as ReadViewWindow.s_panelChoiceIsExplicit: the
    /// page is torn down and rebuilt every time the Matters window is reopened, so an instance
    /// field would forget the user's choice the moment the window closes. Never reset - it lives
    /// for the app run.</summary>
    private static bool s_panelChoiceIsExplicit;

    // The panel/threads instances currently wired to OnPanelPropertyChanged/OnThreadsPropertyChanged
    // - tracked explicitly so a later Assistant swap (or page Unloaded) unsubscribes the RIGHT
    // instance rather than whatever _vm.Assistant happens to hold at that later moment.
    private AssistantSidePanelViewModel? _subscribedPanel;
    private AssistantChatThreadsViewModel? _subscribedThreads;

    public MattersPage(MattersPageViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Re-subscribed on every Assistant swap (matter selection), not just once - see
        // OnVmPropertyChanged.
        _vm.PropertyChanged += OnVmPropertyChanged;
        SubscribeToPanel(_vm.Assistant?.Panel);       // covers the (unlikely) case Assistant was
                                                       // already set before this page loaded
        await _vm.RefreshAsync();                     // deterministic refresh on navigation (design 3.1)
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        SavePanelStateIfExplicit();
        UnsubscribeFromPanel();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MattersPageViewModel.Assistant)) return;
        UnsubscribeFromPanel();
        SubscribeToPanel(_vm.Assistant?.Panel);
    }

    /// <summary>Applies the persisted/heuristic open state to a newly-selected matter's panel and
    /// wires the width-tracking subscription. Saved state (an explicit choice) always wins. With
    /// no saved state, RebuildAssistant's Panel.LoadAsync is fire-and-forget, so
    /// Threads.HasAnyHistory is not yet trustworthy here (a brand-new MatterAssistantViewModel's
    /// Threads always starts at its default HasAnyHistory=false anyway) - instead, subscribe to
    /// the new Threads and open the panel if/when history load flips HasAnyHistory true.</summary>
    private void SubscribeToPanel(AssistantSidePanelViewModel? panel)
    {
        if (panel is null) return;
        _subscribedPanel = panel;
        panel.PropertyChanged += OnPanelPropertyChanged;
        var saved = _vm.PanelStateStore?.LoadAssistantPanel(PanelKey);
        s_panelChoiceIsExplicit |= saved is not null;
        ApplyPanelWidth(saved?.Width ?? PanelDefaultWidth);
        if (saved is not null)
        {
            panel.IsOpen = saved.Open;                // explicit persisted choice wins outright
            return;
        }
        _subscribedThreads = panel.Threads;
        _subscribedThreads.PropertyChanged += OnThreadsPropertyChanged;
    }

    /// <summary>Heuristic completion: the deferred half of SubscribeToPanel's "no saved state"
    /// branch. Only ever opens the panel (never closes it) and only while no explicit choice has
    /// been made for the family - a click landing before this fires must win.</summary>
    private void OnThreadsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantChatThreadsViewModel.HasAnyHistory)) return;
        if (s_panelChoiceIsExplicit) return;
        if (sender is AssistantChatThreadsViewModel { HasAnyHistory: true } && _subscribedPanel is { } panel)
            panel.IsOpen = true;
    }

    private void UnsubscribeFromPanel()
    {
        if (_subscribedPanel is not null) _subscribedPanel.PropertyChanged -= OnPanelPropertyChanged;
        if (_subscribedThreads is not null) _subscribedThreads.PropertyChanged -= OnThreadsPropertyChanged;
        _subscribedPanel = null;
        _subscribedThreads = null;
    }

    private void ApplyPanelWidth(double width)
        => _panelWidth = Math.Max(PanelMinWidth, Math.Min(width, ActualWidth * 0.6));

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AssistantSidePanelViewModel.IsOpen)) return;
        if (sender is not AssistantSidePanelViewModel panel) return;
        if (panel.IsOpen)
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
    /// this wins for the Matters page for the rest of the app run.</summary>
    private void OnAskToggleClick(object sender, RoutedEventArgs e) => s_panelChoiceIsExplicit = true;

    private void SavePanelStateIfExplicit()
    {
        if (!s_panelChoiceIsExplicit) return;
        if (_vm.PanelStateStore is not { } store) return;
        if (_vm.Assistant?.Panel is not { } panel) return;
        store.SaveAssistantPanel(PanelKey, new AssistantPanelState(panel.IsOpen,
            panel.IsOpen ? PanelColumn.Width.Value : _panelWidth));
    }

    private void OnCreateMatter(object sender, RoutedEventArgs e) => _vm.CreateMatterCommand.Execute(null);
    private void OnRepairIndex(object sender, RoutedEventArgs e) => _vm.RepairIndexCommand.Execute(null);
    private async void OnRerenderTagged(object sender, RoutedEventArgs e) => await _vm.RerenderTaggedAsync();
    private void OnDetailCommit(object sender, RoutedEventArgs e) => _vm.CommitDetailCommand.Execute(null);
    private void OnAddMember(object sender, RoutedEventArgs e) => _vm.AddMemberCommand.Execute(null);

    private async void OnMatterSelected(object sender, SelectionChangedEventArgs e)
        => await _vm.SelectAsync((MatterList.SelectedItem as MattersIndexEntry)?.Id);

    private async void OnMemberRename(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.Tag is string memberId)
            await _vm.RenameMemberAsync(memberId, box.Text);
    }

    private async void OnMemberRemove(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string memberId)
            await _vm.RemoveMemberAsync(memberId);
    }

    private void OnOpenTranscript(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTagged is { } t) _vm.OpenTranscript(t.SessionId);
    }

    private void OnTaggedRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vm.SelectedTagged is { } t) _vm.OpenTranscript(t.SessionId);
    }

    private void OnOpenDetails(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTagged is { } t) _vm.JumpToSession(t.SessionId);
    }

    /// <summary>Sessions tab Summary column (Phase 3): a Done/Stale chip opens the read view's
    /// assistant panel on that session (no regenerate).</summary>
    private void OnOpenSummary(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaggedSessionItem row)
            _vm.OpenSummaryCommand.Execute(row);
    }

    /// <summary>Sessions tab Summary column (Phase 3): the "Generate" link for a session with no
    /// summary yet opens the read view's panel and starts a regeneration in one step.</summary>
    private void OnGenerateSummary(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaggedSessionItem row)
            _vm.GenerateSummaryCommand.Execute(row);
    }

    /// <summary>Untag confirm (design 5.4): Yes/No dialog mirroring OnDeleteMatter. The
    /// open-window pre-check answers "close it first" BEFORE the confirm; UntagSessionAsync
    /// re-checks at execution time (the authoritative, unit-tested guard).</summary>
    private async void OnUntagSelected(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedTagged is not { } t) return;
        if (!_vm.CanUntag(t.SessionId))
        {
            MessageBox.Show(
                "This session is open in another window (Session Details or read view). Close it first, then untag.",
                "Untag session", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = MessageBox.Show(
            $"Untag this session from \"{_vm.EditName}\"? The session itself is kept; only the matter tag is removed.",
            "Untag session", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) await _vm.UntagSessionAsync(t.SessionId);
    }

    /// <summary>Add-sessions picker (design 2026-07-18 section 4): dialog owned by the main
    /// window; OK applies the batch through the VM's SaveMetaAsync delta path.</summary>
    private async void OnAddSessions(object sender, RoutedEventArgs e)
    {
        var candidates = await _vm.ListUntaggedSessionsAsync();
        var picker = new AddSessionsPickerViewModel(candidates);
        var dialog = new AddSessionsDialog(picker) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await _vm.AddSessionsAsync(picker.SelectedIds);
    }

    private async void OnExportMatter(object sender, RoutedEventArgs e) => await _vm.ExportMatterArchiveAsync();
    private void OnCancelExport(object sender, RoutedEventArgs e) => _vm.CancelExport();

    private void OnDeleteMatter(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Delete this matter? Its folder goes to the Recycle Bin. Sessions are never deleted by this action.",
            "Delete matter", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes) _vm.DeleteMatterCommand.Execute(null);
    }
}

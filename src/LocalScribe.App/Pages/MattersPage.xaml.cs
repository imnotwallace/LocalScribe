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

    public MattersPage(MattersPageViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await _vm.RefreshAsync();          // deterministic refresh on navigation (design 3.1)
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

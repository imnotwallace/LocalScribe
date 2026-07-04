using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Pages;

/// <summary>Thin code-behind for the Sessions page. Constructed by the composition root with
/// its VM (never via a navigation URI, so the non-default ctor is fine). Loaded fires on every
/// navigation into view, which is exactly the 3.1 "page navigation" refresh trigger; LoadAsync
/// catches all exceptions, so the async-void Loaded lambda cannot throw.</summary>
public partial class SessionsPage : Page
{
    private readonly SessionsPageViewModel _vm;
    private readonly MetadataEditorViewModel _editor;
    private readonly PropertyChangedEventHandler _onVmPropertyChanged;
    // Drives the 2s Saved-indicator countdown; the VM stays timer-free (house rule).
    private readonly System.Windows.Threading.DispatcherTimer _editorTick;

    public SessionsPage(SessionsPageViewModel vm, MetadataEditorViewModel editor)
    {
        _vm = vm;
        _editor = editor;
        DataContext = vm;
        InitializeComponent();
        DetailPane.DataContext = editor;

        _onVmPropertyChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsPageViewModel.SelectedRow))
                editor.Attach(vm.SelectedRow);
        };
        _editorTick = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(250) };
        _editorTick.Tick += (_, _) => editor.Tick();

        // The singleton VMs outlive this page (a fresh SessionsPage is built per MainWindow open,
        // design section 2), so these subscriptions MUST be unhooked on Unloaded - otherwise each
        // reopen leaks a handler and after N reopens one delete raises N confirm dialogs (a single
        // Yes then bypasses a No). Loaded can fire more than once (re-navigation), so hook
        // idempotently (-= then +=) and re-arm the tick, mirroring the previous editorTick handling.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.ConfirmDeleteRequested -= OnConfirmDeleteRequested;
        _vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;
        _vm.PropertyChanged -= _onVmPropertyChanged;
        _vm.PropertyChanged += _onVmPropertyChanged;
        _editor.Attach(_vm.SelectedRow);
        if (!_editorTick.IsEnabled) _editorTick.Start();
        _ = _vm.OnNavigatedToAsync();          // 3.1 page-navigation refresh; LoadAsync catches all
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.ConfirmDeleteRequested -= OnConfirmDeleteRequested;
        _vm.PropertyChanged -= _onVmPropertyChanged;
        _editorTick.Stop();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenReadViewCommand.Execute(_vm.SelectedRow);

    // Dismiss the details drawer: clearing the grid selection nulls SelectedRow (two-way),
    // which detaches the editor and collapses the drawer via its Visibility trigger.
    private void OnCloseDetails(object sender, RoutedEventArgs e)
        => SessionsGrid.SelectedItem = null;

    /// <summary>Modal confirm per design 3.4; invokes onConfirmed synchronously on Yes, which is
    /// exactly the contract SessionsPageViewModel.ConfirmDeleteRequested documents.</summary>
    private void OnConfirmDeleteRequested(DeleteConfirmation payload, Action onConfirmed)
    {
        string matters = payload.MatterNames.Count == 0 ? "(none)" : string.Join(", ", payload.MatterNames);
        string message = payload.Title + "\n" + payload.DateDisplay + "   " + payload.DurationDisplay
            + "\nMatters: " + matters + "\n\n"
            + "This sends the entire session folder - audio, transcript, and metadata - to the Windows Recycle Bin.";
        if (MessageBox.Show(message, "Delete session?", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            onConfirmed();
    }
}

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

    public SessionsPage(SessionsPageViewModel vm, MetadataEditorViewModel editor)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await vm.OnNavigatedToAsync();
        _vm.ConfirmDeleteRequested += OnConfirmDeleteRequested;

        DetailPane.DataContext = editor;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionsPageViewModel.SelectedRow))
                editor.Attach(vm.SelectedRow);
        };
        editor.Attach(vm.SelectedRow);
        // Drives the 2s Saved-indicator countdown; the VM stays timer-free (house rule).
        var editorTick = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(250) };
        editorTick.Tick += (_, _) => editor.Tick();
        editorTick.Start();
        // Pages are rebuilt per MainWindow open (design section 2) - stop the tick with the
        // page so closed windows do not accumulate live timers.
        Unloaded += (_, _) => editorTick.Stop();
        Loaded += (_, _) => { if (!editorTick.IsEnabled) editorTick.Start(); };
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenReadViewCommand.Execute(_vm.SelectedRow);

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

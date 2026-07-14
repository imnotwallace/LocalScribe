using System.Windows;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

public partial class ImportDialog : Window
{
    public ImportDialog(ImportDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += Close;
        // X / Alt+F4 while an import runs: first press CANCELS the import (the importer deletes
        // the partial folder); the dialog stays open until the cancellation unwinds, then a
        // second press (or the Cancel button, now idle) closes it. Never orphan a running import.
        Closing += (_, e) =>
        {
            if (vm.IsBusy) { vm.CancelCommand.Execute(null); e.Cancel = true; }
        };
    }
}

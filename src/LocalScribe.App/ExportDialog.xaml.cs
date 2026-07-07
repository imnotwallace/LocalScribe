using System.Windows;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

public partial class ExportDialog : Window
{
    public ExportDialog(ExportDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += Close;   // export succeeded -> close the dialog
    }
}

using System.Windows;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

public partial class RetranscribeDialog : Window
{
    public RetranscribeDialog(RetranscribeDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += Close;                                  // run succeeded -> close the dialog
        Loaded += (_, _) => _ = vm.LoadAsync(CancellationToken.None);
        Closed += (_, _) => vm.Dispose();                    // detach the runner subscriptions
    }
}

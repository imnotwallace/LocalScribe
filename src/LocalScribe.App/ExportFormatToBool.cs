using System.Globalization;
using System.Windows.Data;

namespace LocalScribe.App.ViewModels;

/// <summary>Two-way binds the ExportFormat enum to a RadioButton's IsChecked (one converter per value).
/// References WPF (System.Windows.Data), so it lives in the App project as its own file - kept OUT of
/// ExportDialogViewModel.cs, which must stay WPF-free.</summary>
public sealed class ExportFormatToBool : IValueConverter
{
    public static readonly ExportFormatToBool Zip = new() { _target = ExportFormat.Zip };
    public static readonly ExportFormatToBool Docx = new() { _target = ExportFormat.Docx };
    private ExportFormat _target;
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is ExportFormat f && f == _target;
    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => value is true ? _target : Binding.DoNothing;
}

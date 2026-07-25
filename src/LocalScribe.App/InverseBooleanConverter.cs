// src/LocalScribe.App/InverseBooleanConverter.cs
using System.Globalization;
using System.Windows.Data;
namespace LocalScribe.App;

/// <summary>Negates a bool (voiceprint design 2026-07-25): disables the Split-speakers
/// "Remember voice" checkbox while a cluster row still carries its default label
/// (<c>ClusterRowViewModel.IsDefaultNamed</c>), mirroring the exact gate
/// <c>SplitSpeakersViewModel.EnrollConfirmedVoicesAsync</c> applies at confirm time so the
/// checkbox never offers an action the confirm silently ignores.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

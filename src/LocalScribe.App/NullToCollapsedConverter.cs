// src/LocalScribe.App/NullToCollapsedConverter.cs
using System.Globalization;
using System.Windows;
using System.Windows.Data;
namespace LocalScribe.App;

/// <summary>Visibility.Collapsed for null, Visible otherwise (voiceprint design 2026-07-25):
/// hides the Split-speakers suggestion chip entirely when a cluster row carries no suggestion,
/// and hides the "linked" indicator when nothing has been accepted - never an empty placeholder
/// row. Generic over any reference type (VoiceprintSuggestion?, string?); one-way only.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

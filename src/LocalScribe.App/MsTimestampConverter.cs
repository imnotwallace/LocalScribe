// src/LocalScribe.App/MsTimestampConverter.cs
using System.Globalization;
using System.Windows.Data;
namespace LocalScribe.App;

/// <summary>Formats a long-ms split-child start as mm:ss.ff (hundredths; h:mm:ss.ff past an hour)
/// and parses it back onto a 10 ms grid (design §3.3). Editable ONLY on split children; whole
/// segments render whole-second stamps via ReadViewStampConverter.</summary>
public sealed class MsTimestampConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long ms) return "";
        var t = TimeSpan.FromMilliseconds(ms);
        string hh = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{hh}{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Accept [h:]mm:ss.ff. Parse leniently; snap to 10 ms.
        var parts = ((string)value).Split(':');
        double seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        int minutes = parts.Length >= 2 ? int.Parse(parts[^2], CultureInfo.InvariantCulture) : 0;
        int hours = parts.Length >= 3 ? int.Parse(parts[^3], CultureInfo.InvariantCulture) : 0;
        long ms = (long)Math.Round((hours * 3600 + minutes * 60 + seconds) * 1000);
        return (long)Math.Round(ms / 10.0) * 10;
    }
}

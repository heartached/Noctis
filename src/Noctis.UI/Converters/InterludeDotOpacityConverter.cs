using Avalonia.Data.Converters;
using Noctis.Services;
using System;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// LyricLine.InterludeProgress → one dot's opacity (<see cref="InterludeDots.DotLevel"/>).
/// Parameter: the dot's index ("0", "1", "2"). A plain binding, no transition: the value
/// changes every frame, and a transition restarted under a per-frame binding stalls.
/// </summary>
public sealed class InterludeDotOpacityConverter : IValueConverter
{
    public static readonly InterludeDotOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double d ? d : double.NaN;
        var index = parameter is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0;
        return InterludeDots.DotLevel(progress, index);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

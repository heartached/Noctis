using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Noctis.Converters;

/// <summary>
/// Picks the font size for a Capture Mode lyric line. The active line uses the
/// user-controlled size (the font-size slider); inactive lines use a fixed
/// smaller size. Expects two bound values: [bool isActive, double activeSize].
/// </summary>
public sealed class ActiveLineFontSizeConverter : IMultiValueConverter
{
    /// <summary>Font size used for every non-active line.</summary>
    private const double InactiveFontSize = 48;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = values.Count > 0 && values[0] is bool b && b;
        if (isActive && values.Count > 1 && values[1] is double activeSize)
            return activeSize;
        return InactiveFontSize;
    }
}

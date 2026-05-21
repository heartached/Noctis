using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;

namespace Noctis.Converters;

/// <summary>
/// Produces the render-scale for a Capture Mode lyric line. Every line is laid
/// out at the active font size; the active line renders at scale 1.0 while
/// inactive lines render scaled down to <see cref="InactiveFontSize"/>.
/// Scaling via a transform (instead of animating FontSize) keeps the list
/// layout fixed, so the active line grows smoothly without reflowing — and
/// shaking — the list during a scroll. Expects two bound values:
/// [bool isActive, double activeFontSize].
/// </summary>
public sealed class ActiveLineScaleConverter : IMultiValueConverter
{
    /// <summary>Rendered size of every non-active line.</summary>
    private const double InactiveFontSize = 38;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var isActive = values.Count > 0 && values[0] is bool b && b;
        var activeSize = values.Count > 1 && values[1] is double d && d > 0 ? d : InactiveFontSize;
        var scale = isActive ? 1.0 : InactiveFontSize / activeSize;
        return TransformOperations.Parse(
            string.Create(CultureInfo.InvariantCulture, $"scale({scale})"));
    }
}

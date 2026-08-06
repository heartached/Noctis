using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Noctis.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Noctis.Converters;

/// <summary>
/// Converters for Apple Music-style duet layout driven by <see cref="LyricLine.Voice"/>:
/// voice 2 lines anchor to the right edge of the lyric column, group ("v3:") lines
/// center, everything else keeps the default left layout. Grouped in one file — they
/// are facets of the same mapping and change together.
/// </summary>
public class LyricVoiceToAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            LyricVoice.Voice2 => HorizontalAlignment.Right,
            LyricVoice.Group => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Left,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Wrapped plain-text lines: ragged-left for voice 2, centered for group.</summary>
public class LyricVoiceToTextAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            LyricVoice.Voice2 => TextAlignment.Right,
            LyricVoice.Group => TextAlignment.Center,
            _ => TextAlignment.Left,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>Karaoke word rows inside the WrapPanel follow the line's edge the same way.</summary>
public class LyricVoiceToItemsAlignmentConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            LyricVoice.Voice2 => WrapPanelItemsAlignment.End,
            LyricVoice.Group => WrapPanelItemsAlignment.Center,
            _ => WrapPanelItemsAlignment.Start,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// Active-line scale origin: pinned to the line's anchored edge so the spring grows
/// in place (right-aligned lines would otherwise drift out past the column edge).
/// </summary>
public class LyricVoiceToTransformOriginConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            LyricVoice.Voice2 => RelativePoint.Parse("1,0.5"),
            LyricVoice.Group => RelativePoint.Parse("0.5,0.5"),
            _ => RelativePoint.Parse("0,0.5"),
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

/// <summary>
/// [HasDuetLines, MaxWidth] → MinWidth for the lyric column. Duet files pin the
/// column to its full MaxWidth so voice-2 lines right-align against a stable edge;
/// single-voice files return 0 and keep the content-hugging layout untouched.
/// </summary>
public class DuetMinWidthConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 2 && values[0] is bool hasDuet && values[1] is double maxWidth
            && hasDuet && !double.IsInfinity(maxWidth) && !double.IsNaN(maxWidth))
        {
            return maxWidth;
        }
        return 0.0;
    }
}

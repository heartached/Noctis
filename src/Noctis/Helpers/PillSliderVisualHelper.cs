using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Helpers;

internal static class PillSliderVisualHelper
{
    public static void UpdateVisual(
        Slider slider,
        Control trackBackground,
        Control trackFill,
        Control thumb,
        TranslateTransform thumbTransform,
        double thumbSize,
        double enabledBackgroundOpacity = 1.0,
        double disabledBackgroundOpacity = 0.45)
    {
        var width = slider.Bounds.Width;
        if (width <= 0)
            return;

        var thumbRadius = thumbSize / 2.0;
        var trackWidth = Math.Max(0, width - thumbSize);
        var fraction = GetFraction(slider);
        var fillWidth = Math.Clamp(trackWidth * fraction, 0, trackWidth);

        trackBackground.Width = trackWidth;
        Canvas.SetLeft(trackBackground, thumbRadius);

        trackFill.Width = Math.Min(trackWidth, fillWidth + thumbRadius);
        Canvas.SetLeft(trackFill, thumbRadius);

        thumbTransform.X = fillWidth;

        var enabled = slider.IsEnabled;
        thumb.Opacity = enabled ? 1.0 : 0.45;
        trackBackground.Opacity = enabled ? enabledBackgroundOpacity : disabledBackgroundOpacity;
        trackFill.Opacity = enabled ? 1.0 : 0.45;
    }

    public static double GetValueFromPointer(Slider slider, Point position, double thumbSize)
    {
        if (slider.Bounds.Width <= 0)
            return slider.Minimum;

        var trackWidth = Math.Max(1, slider.Bounds.Width - thumbSize);
        var fraction = Math.Clamp((position.X - thumbSize / 2.0) / trackWidth, 0, 1);
        return slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
    }

    private static double GetFraction(Slider slider)
    {
        var range = slider.Maximum - slider.Minimum;
        if (range <= 0)
            return 0;

        return Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1);
    }
}

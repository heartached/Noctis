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

        var enabled = slider.IsEnabled;
        if (enabled)
        {
            trackBackground.Width = trackWidth;
            Canvas.SetLeft(trackBackground, thumbRadius);

            // End the fill at the thumb's center — the fill bar is thinner than the
            // thumb circle, so anything past the center pokes out beyond its curve.
            trackFill.Width = fillWidth;
            Canvas.SetLeft(trackFill, thumbRadius);
        }
        else
        {
            // Disabled = dimmed, and the dimmed thumb is see-through: the full-width track
            // under the fill, and both under the thumb, showed through as a broken slider
            // (10-05). Fill up to the thumb, track from its far side, square where they meet it,
            // each tucked just far enough under that its corners land on the circle (exactly
            // edge-to-edge left a notch at each join). (Enabled keeps the overlap: some thumbs
            // hide until hover.)
            var tuck = Tuck(thumbRadius, Thickness(trackFill, horizontal: true));
            trackFill.Width = Math.Max(0, fillWidth - thumbRadius + tuck);
            Canvas.SetLeft(trackFill, thumbRadius);
            trackBackground.Width = Math.Max(0, trackWidth - fillWidth - thumbRadius + tuck);
            Canvas.SetLeft(trackBackground, fillWidth + thumbSize - tuck);
        }
        SetInnerCorners(trackFill, trackBackground, horizontal: true, squared: !enabled);

        thumbTransform.X = fillWidth;

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

    /// <summary>
    /// Vertical variant: track runs bottom-to-top (bottom = minimum, top = maximum).
    /// </summary>
    public static void UpdateVisualVertical(
        Slider slider,
        Control trackBackground,
        Control trackFill,
        Control thumb,
        TranslateTransform thumbTransform,
        double thumbSize,
        double enabledBackgroundOpacity = 1.0,
        double disabledBackgroundOpacity = 0.45)
    {
        var height = slider.Bounds.Height;
        if (height <= 0)
            return;

        var thumbRadius = thumbSize / 2.0;
        var trackHeight = Math.Max(0, height - thumbSize);
        var fraction = GetFraction(slider);
        var fillHeight = Math.Clamp(trackHeight * fraction, 0, trackHeight);

        var enabled = slider.IsEnabled;
        if (enabled)
        {
            trackBackground.Height = trackHeight;
            Canvas.SetTop(trackBackground, thumbRadius);

            // End the fill at the thumb's center (see horizontal variant).
            trackFill.Height = fillHeight;
            Canvas.SetTop(trackFill, height - thumbRadius - fillHeight);
        }
        else
        {
            // Dimmed: track above the thumb, fill below it, tucked to the circle (see horizontal variant).
            var thumbTop = trackHeight - fillHeight;
            var tuck = Tuck(thumbRadius, Thickness(trackFill, horizontal: false));
            trackBackground.Height = Math.Max(0, thumbTop - thumbRadius + tuck);
            Canvas.SetTop(trackBackground, thumbRadius);
            trackFill.Height = Math.Max(0, fillHeight - thumbRadius + tuck);
            Canvas.SetTop(trackFill, thumbTop + thumbSize - tuck);
        }
        SetInnerCorners(trackFill, trackBackground, horizontal: false, squared: !enabled);

        thumbTransform.Y = trackHeight - fillHeight;

        thumb.Opacity = enabled ? 1.0 : 0.45;
        trackBackground.Opacity = enabled ? enabledBackgroundOpacity : disabledBackgroundOpacity;
        trackFill.Opacity = enabled ? 1.0 : 0.45;
    }

    public static double GetValueFromPointerVertical(Slider slider, Point position, double thumbSize)
    {
        if (slider.Bounds.Height <= 0)
            return slider.Minimum;

        var trackHeight = Math.Max(1, slider.Bounds.Height - thumbSize);
        var fraction = Math.Clamp(1.0 - (position.Y - thumbSize / 2.0) / trackHeight, 0, 1);
        return slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
    }

    /// <summary>How far a bar of <paramref name="thickness"/> runs under a round thumb so its square
    /// corners sit on the circle: r − √(r² − (t/2)²), plus a hair for antialiasing.</summary>
    private static double Tuck(double thumbRadius, double thickness)
    {
        var half = Math.Min(thickness / 2, thumbRadius);
        return thumbRadius - Math.Sqrt(thumbRadius * thumbRadius - half * half) + 0.15;
    }

    private static double Thickness(Control bar, bool horizontal)
    {
        var set = horizontal ? bar.Height : bar.Width;
        if (!double.IsNaN(set) && set > 0) return set;
        return horizontal ? bar.Bounds.Height : bar.Bounds.Width;
    }

    /// <summary>Each bar's own (XAML) corner radius, kept so the enabled layout gets it back.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Border, object> OriginalCorners = new();

    /// <summary>When <paramref name="squared"/> (the dimmed, edge-to-edge layout), squares the ends
    /// that meet the thumb and keeps the outer ends round; otherwise restores the bar's own corners.
    /// Borders only.</summary>
    private static void SetInnerCorners(Control fill, Control background, bool horizontal, bool squared)
    {
        Apply(fill, isFill: true);
        Apply(background, isFill: false);

        void Apply(Control bar, bool isFill)
        {
            if (bar is not Border border) return;
            var original = (CornerRadius)OriginalCorners.GetValue(border, b => b.CornerRadius);
            if (!squared)
            {
                border.CornerRadius = original;
                return;
            }
            var r = Math.Max(Math.Max(original.TopLeft, original.TopRight), Math.Max(original.BottomLeft, original.BottomRight));
            // Horizontal: fill on the left, track on the right. Vertical: fill below, track above.
            border.CornerRadius = horizontal
                ? (isFill ? new CornerRadius(r, 0, 0, r) : new CornerRadius(0, r, r, 0))
                : (isFill ? new CornerRadius(0, 0, r, r) : new CornerRadius(r, r, 0, 0));
        }
    }

    private static double GetFraction(Slider slider)
    {
        var range = slider.Maximum - slider.Minimum;
        if (range <= 0)
            return 0;

        return Math.Clamp((slider.Value - slider.Minimum) / range, 0, 1);
    }
}

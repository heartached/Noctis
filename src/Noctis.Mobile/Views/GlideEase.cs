using Noctis.Helpers;

namespace Noctis.Mobile.Views;

/// <summary>
/// The phone's settle curve (Appear, the Queue sheet, the tab bar: cubic-bezier 0.32, 0.72,
/// 0, 1) as an easing XAML can create. CubicBezierEase takes its points in the constructor,
/// so a style's transition cannot name it, and SplineEasing is broken here.
/// </summary>
public sealed class GlideEase : Avalonia.Animation.Easings.Easing
{
    private static readonly CubicBezierEase Curve = new(0.32, 0.72, 0, 1);

    public override double Ease(double progress) => Curve.Ease(progress);
}

using System;
using Avalonia;
using Avalonia.Media;
using Noctis.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// The tab bar's selection pill as a Liquid Glass droplet (Apple Music, iOS 26). At rest it
/// is a soft grey glass pill: its <see cref="GlassPanel.Background"/> tinted over the bar,
/// with no backdrop read at all (cheap). <see cref="Lift"/> 0..1 raises it while it travels
/// between tabs: it grows a little, clears its tint, magnifies what lies under it, its rim
/// refracts and splits the colours, and a rim light runs round it — then it settles back.
///
/// One property drives the whole look so the shell can move it with one plain transition
/// (set to 1 on a tab tap, back to 0 as the slide lands): nothing is restarted per frame.
/// </summary>
public class GlassDroplet : GlassPanel
{
    public static readonly StyledProperty<double> LiftProperty =
        AvaloniaProperty.Register<GlassDroplet, double>(nameof(Lift));

    /// <summary>The lifted droplet's rim lens band and reach (logical px), its zoom and how
    /// much it grows across (X) and up (Y). Tuned on the emulator against the owner's
    /// Apple Music reference.</summary>
    internal const double LiftedBand = 14, LiftedBend = 16, LiftedZoom = 1.12, GrowX = 0.08, GrowY = 0.12;

    private readonly ScaleTransform _scale = new(1, 1);

    public GlassDroplet()
    {
        // The droplet grows about its centre; the shell moves it through its host.
        RenderTransform = _scale;
        RenderTransformOrigin = RelativePoint.Center;
    }

    /// <summary>0 at rest, 1 fully lifted.</summary>
    public double Lift { get => GetValue(LiftProperty); set => SetValue(LiftProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LiftProperty) ApplyLift(Lift);
    }

    /// <summary>Maps the lift onto the glass. At exactly 0 every setting is back at its
    /// default, so the resting pill reads no backdrop (see <see cref="GlassPanel.NeedsBackdrop"/>).</summary>
    private void ApplyLift(double lift)
    {
        var l = Math.Clamp(lift, 0, 1);
        // Smoothstep: the lens and the colour split ease in and out of the transition's ends.
        var s = l * l * (3 - 2 * l);
        Refraction = LiftedBand * s;
        RefractionAmount = LiftedBend * s;
        Magnification = 1 + (LiftedZoom - 1) * s;
        Dispersion = s;
        Specular = s;
        // Lifted, the droplet is clear glass: its grey tint fades out.
        GlassTintOpacity = l <= 0 ? null : RestTintOpacity() * (1 - 0.85 * s);
        // Lifted off the bar, it casts a soft shadow onto it.
        BoxShadow = s <= 0 ? default : new BoxShadows(new BoxShadow
        {
            OffsetY = 3 * s, Blur = 14 * s, Color = Color.FromArgb((byte)Math.Round(0x38 * s), 0, 0, 0),
        });
        _scale.ScaleX = 1 + GrowX * s;
        _scale.ScaleY = 1 + GrowY * s;
    }

    private double RestTintOpacity() =>
        Background is ISolidColorBrush b ? b.Opacity * (b.Color.A / 255.0) : 0.5;
}

using Avalonia;
using Avalonia.Media;

namespace Noctis.Mobile.Services;

/// <summary>
/// A soft mesh as one brush: a floor colour under three radial washes, drawn as a single
/// DrawingBrush (a handful of gradients, stretched over whatever it paints). Built once; a new
/// cover, a new strength and the slow drift only change the colours and centres of its parts,
/// so nothing is allocated per frame and every surface using the brush repaints together.
/// </summary>
internal sealed class MeshBrush
{
    // Where each wash sits (fractions of the page) and how wide it spreads.
    private static readonly (double X, double Y, double Radius)[] Homes =
    {
        (0.20, 0.10, 0.95),
        (0.90, 0.62, 0.85),
        (0.30, 0.98, 0.75),
    };

    private readonly SolidColorBrush _floor = new(Colors.Black);
    private readonly RadialGradientBrush[] _washes = new RadialGradientBrush[Homes.Length];
    private readonly GradientStop[] _centre = new GradientStop[Homes.Length];
    private readonly GradientStop[] _edge = new GradientStop[Homes.Length];

    public MeshBrush()
    {
        var group = new DrawingGroup();
        var rect = new RectangleGeometry(new Rect(0, 0, 100, 100));
        group.Children.Add(new GeometryDrawing { Brush = _floor, Geometry = rect });
        for (var i = 0; i < Homes.Length; i++)
        {
            _centre[i] = new GradientStop(Colors.Transparent, 0);
            _edge[i] = new GradientStop(Colors.Transparent, 1);
            _washes[i] = new RadialGradientBrush
            {
                Center = new RelativePoint(Homes[i].X, Homes[i].Y, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(Homes[i].X, Homes[i].Y, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(Homes[i].Radius, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(Homes[i].Radius, RelativeUnit.Relative),
                GradientStops = { _centre[i], _edge[i] },
            };
            group.Children.Add(new GeometryDrawing { Brush = _washes[i], Geometry = rect });
        }
        Brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
    }

    public DrawingBrush Brush { get; }

    /// <summary>The wash's opacity at its centre for a Strength of 0.2 … 1: faint but present at the
    /// bottom, the full colour at the top.</summary>
    public static double WashAlpha(double strength) => 0.3 + 0.7 * Math.Clamp(strength, 0, 1);

    public void Set(BackgroundCatalog.Palette palette, double strength)
    {
        var alpha = (byte)Math.Round(255 * WashAlpha(strength));
        _floor.Color = palette.Floor;
        Wash(0, palette.A, alpha);
        Wash(1, palette.B, alpha);
        Wash(2, palette.C, alpha);
        Invalidate();
    }

    private void Wash(int i, Color colour, byte alpha)
    {
        _centre[i].Color = Color.FromArgb(alpha, colour.R, colour.G, colour.B);
        _edge[i].Color = Color.FromArgb(0, colour.R, colour.G, colour.B);
    }

    /// <summary>Moves each wash a little along its own slow loop; <paramref name="seconds"/> is
    /// the time since the drift began. The same loops, one phase apart, keep it from ever repeating
    /// in step.</summary>
    public void Drift(double seconds)
    {
        for (var i = 0; i < Homes.Length; i++)
        {
            var a = seconds * (0.11 + 0.037 * i) + i * 2.1;
            var p = new RelativePoint(Homes[i].X + 0.12 * Math.Sin(a), Homes[i].Y + 0.09 * Math.Cos(a * 1.3), RelativeUnit.Relative);
            _washes[i].Center = p;
            _washes[i].GradientOrigin = p;
        }
        Invalidate();
    }

    private bool _nudge;

    /// <summary>A DrawingBrush does not notice its drawing's gradients changing: a surface showing it
    /// kept the old frame (the background tests catch this). Its own opacity does repaint it, so
    /// each change flips that by a hair (invisible: 0.2 %).</summary>
    private void Invalidate() => Brush.Opacity = (_nudge = !_nudge) ? 0.998 : 1;

    /// <summary>Back to the washes' resting places.</summary>
    public void Rest()
    {
        for (var i = 0; i < Homes.Length; i++)
        {
            var p = new RelativePoint(Homes[i].X, Homes[i].Y, RelativeUnit.Relative);
            _washes[i].Center = p;
            _washes[i].GradientOrigin = p;
        }
        Invalidate();
    }
}

using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// One of the app's stroked line icons (the <c>MenuLine*</c> / <c>Line*</c> geometries in
/// Icons.axaml and IconsLine.axaml) drawn outside a menu: pop-up header tiles, dialog
/// buttons, the command palette. Owner 10-09: "match all the icons for the same action
/// everywhere" — the Add to Playlist dialog now shows the menu row's own glyph, not a PNG.
///
/// Draws exactly what <c>MenuV2.LineIcon</c> draws (a Path in a 24×24 Canvas inside a
/// Viewbox): the geometry is scaled from its 24-unit grid to the control's size and stroked
/// with round caps/joins at <see cref="StrokeThickness"/> grid units, so 2.25 is 1.5px at
/// 16px and the strokes thicken in step with the size. No fill — a PathIcon would fill an
/// open stroke into a blob. Vector only, so it renders the same on Windows, macOS and Linux
/// (no font or PNG involved).
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    /// <summary>Stroke colour; inherits the surrounding text colour like PathIcon does.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    /// <summary>Stroke width in grid units (the icons' 24-unit grid), not pixels.</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 2.25);

    /// <summary>Also fill the shape (a lit star, a favorited heart). Off by default.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LineIcon, IBrush?>(nameof(Fill));

    /// <summary>The grid the geometries are drawn on.</summary>
    public const double Grid = 24;

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty, FillProperty);
        AffectsMeasure<LineIcon>(DataProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>16px unless Width/Height say otherwise (the menus' size).</summary>
    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        var data = Data;
        var brush = Foreground;
        if (data is null || brush is null)
            return;

        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0)
            return;

        // Uniform scale of the 24 grid, centred — what a Uniform Viewbox around a 24×24
        // Canvas does.
        var scale = side / Grid;
        var dx = (Bounds.Width - side) / 2;
        var dy = (Bounds.Height - side) / 2;
        var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
            context.DrawGeometry(Fill, pen, data);
    }
}

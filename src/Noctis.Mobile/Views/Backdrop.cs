using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Mobile.Views;

/// <summary>
/// Paints Settings › Background's page brush (MeshBrush, WashBrush: a DrawingBrush stretched over
/// the page) by drawing its drawing straight onto the page. As a brush, Avalonia's Skia backend
/// records the drawing again and rasterizes it into a page-sized picture on every paint
/// (DrawingContextImpl.ConfigureSceneBrushContentWithPicture), and the page brush is painted under
/// the window, the tab roots and the sheet at once: scrolling Settings over a Background dropped
/// the S23 from 120 to 60 fps with 25-75 ms frames (beta 6 "the lag", 11-27), where Off scrolled
/// at 120. Drawn directly it is a few plain fills (the floor, the gradients, the cover's image).
/// Any other brush paints as before. The brush's own opacity is the meshes' repaint nudge (0.998
/// or 1, <see cref="Mobile.Services.MeshBrush"/>): at 0.99 and over it is drawn opaque, and each
/// nudge records the page again (a swapped cover or a fade step is not seen otherwise).
/// </summary>
internal static class BackdropPaint
{
    /// <summary>Whether <paramref name="brush"/> is painted by drawing its drawing: a DrawingBrush
    /// that fills its target exactly (Stretch Fill, no tiling, transform, viewbox or viewport).</summary>
    public static bool DrawsDirectly(IBrush? brush) =>
        brush is DrawingBrush { Drawing: { } drawing, Stretch: Stretch.Fill, TileMode: TileMode.None, Transform: null } b
        && b.SourceRect == RelativeRect.Fill && b.DestinationRect == RelativeRect.Fill
        && drawing.GetBounds() is { Width: > 0, Height: > 0 };

    /// <summary>The drawing's own space (its bounds) onto <paramref name="target"/>, as Stretch Fill maps it.</summary>
    public static Matrix FillTransform(Rect content, Rect target) =>
        Matrix.CreateTranslation(-content.X, -content.Y)
        * Matrix.CreateScale(target.Width / content.Width, target.Height / content.Height)
        * Matrix.CreateTranslation(target.X, target.Y);

    /// <summary>Draws <paramref name="brush"/> over <paramref name="target"/> (rounded by
    /// <paramref name="corners"/>) when it <see cref="DrawsDirectly"/>; false leaves it to the control.</summary>
    public static bool TryPaint(DrawingContext context, IBrush? brush, Rect target, CornerRadius corners = default)
    {
        if (!DrawsDirectly(brush) || target.Width <= 0 || target.Height <= 0) return false;
        var drawingBrush = (DrawingBrush)brush!;
        var drawing = drawingBrush.Drawing!;
        using (corners != default ? context.PushClip(new RoundedRect(target, corners)) : default)
        using (drawingBrush.Opacity < 0.99 ? context.PushOpacity(drawingBrush.Opacity) : default)
        using (context.PushTransform(FillTransform(drawing.GetBounds(), target)))
            drawing.Draw(context);
        return true;
    }

    /// <summary>Watches a control's page brush while the control is in the tree: each nudge of a
    /// drawn one calls <paramref name="repaint"/> (the control's InvalidateVisual).</summary>
    internal sealed class Watch(Action repaint)
    {
        private AvaloniaObject? _watched;
        private IBrush? _brush;
        private bool _attached;

        public void Brush(IBrush? brush)
        {
            _brush = brush;
            Hook();
        }

        public void Attached(bool attached)
        {
            _attached = attached;
            Hook();
        }

        private void Hook()
        {
            var want = _attached && DrawsDirectly(_brush) ? (AvaloniaObject)_brush! : null;
            if (ReferenceEquals(want, _watched)) return;
            if (_watched != null) _watched.PropertyChanged -= OnBrushChanged;
            _watched = want;
            if (_watched != null) _watched.PropertyChanged += OnBrushChanged;
        }

        private void OnBrushChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Avalonia.Media.Brush.OpacityProperty || e.Property == DrawingBrush.DrawingProperty)
                repaint();
        }
    }
}

/// <summary>
/// The page brush as a layer of its own, behind a page's content (the window, the tab roots, the
/// sheets' rounded backdrop): a Background's mesh is drawn directly (<see cref="BackdropPaint"/>),
/// any other brush filled as a Border would. Hit-tests over its area as a background does.
/// (Panel and Border seal Render, so the brush cannot be drawn their way on the panel itself.)
/// </summary>
public sealed class PageBackdrop : Control
{
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<PageBackdrop, IBrush?>(nameof(Brush));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<PageBackdrop, CornerRadius>(nameof(CornerRadius));

    static PageBackdrop() => AffectsRender<PageBackdrop>(BrushProperty, CornerRadiusProperty);

    private readonly BackdropPaint.Watch _watch;

    public PageBackdrop() => _watch = new BackdropPaint.Watch(InvalidateVisual);

    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BrushProperty) _watch.Brush(Brush);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _watch.Attached(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _watch.Attached(false);
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (Brush is not { } brush || BackdropPaint.TryPaint(context, brush, rect, CornerRadius)) return;
        context.DrawRectangle(brush, null, new RoundedRect(rect, CornerRadius));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;
using Noctis.Models;

namespace Noctis.Mobile.Views;

/// <summary>
/// A song row's check circle in Select mode (Apple Music's): an empty ring, or the accent disc
/// with a white tick once the song is selected. Drawn, not templated: it listens to its page's
/// <see cref="TrackSelection"/> only while on screen, redraws only when its own song is toggled
/// (or every row is: Select mode came or went, Select All), and outside Select mode takes no
/// room, so the row is laid out as it always was.
/// </summary>
public sealed class SelectCheck : Control
{
    public static readonly StyledProperty<TrackSelection?> SelectionProperty =
        AvaloniaProperty.Register<SelectCheck, TrackSelection?>(nameof(Selection));

    public static readonly StyledProperty<Track?> TrackProperty =
        AvaloniaProperty.Register<SelectCheck, Track?>(nameof(Track));

    /// <summary>The empty ring's colour (MobileStyles: the secondary text grey).</summary>
    public static readonly StyledProperty<IBrush?> RingBrushProperty =
        AvaloniaProperty.Register<SelectCheck, IBrush?>(nameof(RingBrush));

    /// <summary>The selected disc's colour (MobileStyles: the accent).</summary>
    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SelectCheck, IBrush?>(nameof(FillBrush));

    /// <summary>The circle's diameter, and the room it keeps after it.</summary>
    public const double Diameter = 23;
    public const double Gap = 12;

    private static readonly Geometry Tick = StreamGeometry.Parse("M9 16.17L4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z");

    private TrackSelection? _watched;
    private Pen? _ringPen;
    private bool _attached;

    static SelectCheck()
    {
        AffectsMeasure<SelectCheck>(SelectionProperty);
        AffectsRender<SelectCheck>(TrackProperty, RingBrushProperty, FillBrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<SelectCheck>(false);
    }

    public TrackSelection? Selection { get => GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    public Track? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? RingBrush { get => GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public IBrush? FillBrush { get => GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    /// <summary>Select mode is on for this row's list (the circle shows).</summary>
    public bool IsShown => Selection is { IsActive: true };

    /// <summary>The row's song is selected (the disc is filled). Tests read it.</summary>
    public bool IsChecked => IsShown && Track is { } t && Selection!.IsSelected(t);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectionProperty) Watch();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch();
    }

    // Off screen it lets go: a recycled or dropped row must not stay on the selection's list.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Watch();
    }

    private void Watch()
    {
        var watch = _attached ? Selection : null;
        if (ReferenceEquals(watch, _watched)) return;
        if (_watched != null) _watched.Changed -= OnSelectionChanged;
        _watched = watch;
        if (_watched != null) _watched.Changed += OnSelectionChanged;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnSelectionChanged(Guid? id)
    {
        if (id == null)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (Track is { } t && t.Id == id.Value)
        {
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        IsShown ? new Size(Diameter + Gap, Diameter) : default;

    public override void Render(DrawingContext context)
    {
        if (!IsShown) return;
        var r = Diameter / 2;
        var center = new Point(r, Bounds.Height / 2);
        if (IsChecked)
        {
            if (FillBrush is { } fill) context.DrawEllipse(fill, null, center, r, r);
            var scale = (Diameter - 8) / 24;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(center.X - 12 * scale, center.Y - 12 * scale)))
                context.DrawGeometry(Brushes.White, null, Tick);
        }
        else if (RingBrush is { } ring)
        {
            if (!ReferenceEquals(_ringPen?.Brush, ring)) _ringPen = new Pen(ring, 1.5);
            context.DrawEllipse(null, _ringPen, center, r - 0.75, r - 0.75);
        }
    }
}

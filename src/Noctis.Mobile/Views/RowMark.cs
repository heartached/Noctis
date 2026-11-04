using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Mobile.ViewModels;
using Noctis.Models;

namespace Noctis.Mobile.Views;

/// <summary>
/// A song row's small mark at the end of its artist line (tester 11-04): the song's star rating
/// (filled stars, only when rated) or a heart for a favourite, as Settings › Library › Show in
/// Song Rows chooses (<see cref="SongRowMark"/>). Drawn, not templated, and bound to nothing:
/// the song is the row's DataContext, the choice is inherited from the shell (ShellView sets
/// <see cref="ModeProperty"/> once), and a heart or rating changed elsewhere (the menu, a sync)
/// redraws it through the song's own PropertyChanged. Unrated, unfavourited or Off, it takes no
/// room, so the artist keeps the whole line.
/// </summary>
public sealed class RowMark : Control
{
    /// <summary>Which mark song rows show; inherited, so one value on the shell reaches every row.</summary>
    public static readonly AttachedProperty<SongRowMark> ModeProperty =
        AvaloniaProperty.RegisterAttached<RowMark, Control, SongRowMark>("Mode", SongRowMark.Ratings, inherits: true);

    /// <summary>The stars' colour (the artist line's grey, MobileStyles).</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RowMark, IBrush?>(nameof(Foreground));

    /// <summary>The heart's colour (the accent, MobileStyles).</summary>
    public static readonly StyledProperty<IBrush?> HeartBrushProperty =
        AvaloniaProperty.Register<RowMark, IBrush?>(nameof(HeartBrush));

    /// <summary>One star's or the heart's side, in dp.</summary>
    public const double GlyphSize = 11;

    public const double StarGap = 1.5;

    // The phone's own glyphs on the 24 grid (PhoneIcons PhStarFill; the desktop's HeartFillIcon).
    private static readonly Geometry Star = StreamGeometry.Parse("M12 17.27L18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z");
    private static readonly Geometry Heart = StreamGeometry.Parse("M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z");

    private Track? _track;

    static RowMark()
    {
        AffectsMeasure<RowMark>(ModeProperty);
        AffectsRender<RowMark>(ModeProperty, ForegroundProperty, HeartBrushProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<RowMark>(false);
    }

    public static SongRowMark GetMode(Control control) => control.GetValue(ModeProperty);
    public static void SetMode(Control control, SongRowMark value) => control.SetValue(ModeProperty, value);

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public IBrush? HeartBrush { get => GetValue(HeartBrushProperty); set => SetValue(HeartBrushProperty, value); }

    /// <summary>The stars shown (0-5), 0 when a heart or nothing shows.</summary>
    public int StarCount => GetValue(ModeProperty) == SongRowMark.Ratings && _track is { } t ? Math.Clamp(t.Rating, 0, 5) : 0;

    /// <summary>Whether the heart shows.</summary>
    public bool ShowsHeart => GetValue(ModeProperty) == SongRowMark.Favorites && _track is { IsFavorite: true };

    /// <summary>Whether anything shows (the row's tests read it).</summary>
    public bool HasMark => StarCount > 0 || ShowsHeart;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch();
    }

    // Off the screen it lets go of the song: a recycled or dropped row must not stay on the
    // song's event list.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Watch();
    }

    private bool _attached;
    private Track? _watched;

    private void Watch()
    {
        _track = DataContext as Track;
        var watch = _attached ? _track : null;
        if (ReferenceEquals(watch, _watched)) return;
        if (_watched != null) _watched.PropertyChanged -= OnTrackChanged;
        _watched = watch;
        if (_watched != null) _watched.PropertyChanged += OnTrackChanged;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Track.Rating) or nameof(Track.IsFavorite))
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var stars = StarCount;
        if (stars > 0) return new Size(stars * GlyphSize + (stars - 1) * StarGap, GlyphSize);
        return ShowsHeart ? new Size(GlyphSize, GlyphSize) : default;
    }

    public override void Render(DrawingContext context)
    {
        var stars = StarCount;
        var heart = ShowsHeart;
        if (stars == 0 && !heart) return;
        var brush = heart ? HeartBrush ?? Foreground : Foreground;
        if (brush == null) return;
        var scale = GlyphSize / 24;
        var y = (Bounds.Height - GlyphSize) / 2;
        for (var i = 0; i < (heart ? 1 : stars); i++)
        {
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(i * (GlyphSize + StarGap), y)))
                context.DrawGeometry(brush, null, heart ? Heart : Star);
        }
    }
}

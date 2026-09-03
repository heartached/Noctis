using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Transformation;

namespace Noctis.Controls;

/// <summary>
/// The app's favorite heart. One control for every site (playback bar, song rows, mini
/// player, lyrics page, Cover Flow overlays…) so the toggle feels the same everywhere:
/// whichever state becomes current pops in — scale 0.7→1 with a fade, 250 ms ease-out —
/// in BOTH directions. Before this, the red heart popped in on favorite while the grey
/// one simply appeared on unfavorite, and half the sites had no animation at all.
///
/// Recycle-safe: a virtualized row that is re-bound to another track flips
/// <see cref="IsFavorite"/> too, and animating that would make hearts pop while
/// scrolling. State changes within a short window after the DataContext changes are
/// applied instantly; a user's click comes much later and animates.
/// </summary>
public sealed class HeartIcon : Panel
{
    public static readonly StyledProperty<bool> IsFavoriteProperty =
        AvaloniaProperty.Register<HeartIcon, bool>(nameof(IsFavorite));

    /// <summary>Edge length of the glyph in px (the control measures to this).</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<HeartIcon, double>(nameof(Size), 14);

    /// <summary>Fill while favorited. The app's heart red.</summary>
    public static readonly StyledProperty<IBrush?> OnBrushProperty =
        AvaloniaProperty.Register<HeartIcon, IBrush?>(nameof(OnBrush), Brush.Parse("#E74856"));

    /// <summary>Fill while not favorited; null inherits the surrounding Foreground.</summary>
    public static readonly StyledProperty<IBrush?> OffBrushProperty =
        AvaloniaProperty.Register<HeartIcon, IBrush?>(nameof(OffBrush));

    /// <summary>Opacity of the not-favorited glyph (a "faint" heart on hover rows).</summary>
    public static readonly StyledProperty<double> OffOpacityProperty =
        AvaloniaProperty.Register<HeartIcon, double>(nameof(OffOpacity), 1);

    /// <summary>False for badge-style overlays that show nothing until favorited.</summary>
    public static readonly StyledProperty<bool> ShowWhenOffProperty =
        AvaloniaProperty.Register<HeartIcon, bool>(nameof(ShowWhenOff), true);

    /// <summary>Draw the off state as a hollow outline instead of a dimmed solid heart —
    /// the "blank heart" a toggle wants, so favorited and not read as fill vs. ring.
    /// On by default (2026-09-03) so every site, mini player included, matches.</summary>
    public static readonly StyledProperty<bool> OutlineWhenOffProperty =
        AvaloniaProperty.Register<HeartIcon, bool>(nameof(OutlineWhenOff), true);

    public bool IsFavorite { get => GetValue(IsFavoriteProperty); set => SetValue(IsFavoriteProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public IBrush? OnBrush { get => GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
    public IBrush? OffBrush { get => GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }
    public double OffOpacity { get => GetValue(OffOpacityProperty); set => SetValue(OffOpacityProperty, value); }
    public bool ShowWhenOff { get => GetValue(ShowWhenOffProperty); set => SetValue(ShowWhenOffProperty, value); }
    public bool OutlineWhenOff { get => GetValue(OutlineWhenOffProperty); set => SetValue(OutlineWhenOffProperty, value); }

    /// <summary>Resource key of the shared heart geometry (Assets/Icons.axaml).</summary>
    private const string GeometryKey = "HeartFillIcon";
    private const string OutlineGeometryKey = "HeartOutlineIcon";

    /// <summary>State changes this soon after a DataContext change are a re-bind, not a click.</summary>
    private const long RebindWindowMs = 150;

    private static readonly TransformOperations Rest = TransformOperations.Parse("scale(1)");
    private static readonly TransformOperations Small = TransformOperations.Parse("scale(0.7)");
    private static Geometry? s_heart;
    private static Geometry? s_heartOutline;

    private readonly PathIcon _on = new();
    private readonly PathIcon _off = new();
    private readonly Transitions _onPop = CreatePop();
    private readonly Transitions _offPop = CreatePop();
    private long _contextChangedAt = long.MinValue;

    static HeartIcon()
    {
        IsFavoriteProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplyState(animate: h.IsSettled));
        ShowWhenOffProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplyState(animate: false));
        SizeProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplySize());
        OnBrushProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplyBrushes());
        OffBrushProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplyBrushes());
        OffOpacityProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplyState(animate: false));
        OutlineWhenOffProperty.Changed.AddClassHandler<HeartIcon>((h, _) => h.ApplyGeometry());
    }

    public HeartIcon()
    {
        foreach (var icon in new[] { _off, _on })
        {
            icon.RenderTransformOrigin = RelativePoint.Center;
            icon.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            icon.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            Children.Add(icon);
        }
        ApplySize();
        ApplyBrushes();
        ApplyState(animate: false);
    }

    /// <summary>The glyph currently showing, or null when off and <see cref="ShowWhenOff"/> is false.</summary>
    internal PathIcon? VisibleGlyph => _on.IsVisible ? _on : _off.IsVisible ? _off : null;

    private bool IsSettled => Environment.TickCount64 - _contextChangedAt > RebindWindowMs;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _contextChangedAt = Environment.TickCount64;
    }

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        // Attaching (a freshly realized row) is a re-bind too.
        _contextChangedAt = Environment.TickCount64;
        if (s_heart == null && this.TryFindResource(GeometryKey, out var res) && res is Geometry g)
            s_heart = g;
        if (s_heartOutline == null && this.TryFindResource(OutlineGeometryKey, out var outline) && outline is Geometry og)
            s_heartOutline = og;
        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        _on.Data = s_heart;
        _off.Data = OutlineWhenOff && s_heartOutline != null ? s_heartOutline : s_heart;
    }

    private static Transitions CreatePop() => new()
    {
        new TransformOperationsTransition
        {
            Property = RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(250),
            Easing = new CubicEaseOut(),
        },
        new DoubleTransition
        {
            Property = OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut(),
        },
    };

    private void ApplySize()
    {
        var s = Math.Max(1, Size);
        _on.Width = _on.Height = s;
        _off.Width = _off.Height = s;
    }

    private void ApplyBrushes()
    {
        _on.Foreground = OnBrush;
        if (OffBrush is { } off) _off.Foreground = off;
        else _off.ClearValue(PathIcon.ForegroundProperty); // inherit the site's Foreground
    }

    /// <summary>
    /// Puts both glyphs in their resting state for the current value. The glyph that is
    /// hidden always rests at scale 0.7 / opacity 0, so when a toggle shows it the
    /// transitions carry it to scale 1 / full opacity — that is the pop. Without
    /// animation the transitions are detached for the assignment so the state snaps.
    /// </summary>
    private void ApplyState(bool animate)
    {
        if (!animate)
        {
            _on.Transitions = null;
            _off.Transitions = null;
        }

        var fav = IsFavorite;
        var showOff = !fav && ShowWhenOff;

        _on.IsVisible = fav;
        _on.Opacity = fav ? 1 : 0;
        _on.RenderTransform = fav ? Rest : Small;

        _off.IsVisible = showOff;
        _off.Opacity = showOff ? OffOpacity : 0;
        _off.RenderTransform = showOff ? Rest : Small;

        if (!animate)
        {
            _on.Transitions = _onPop;
            _off.Transitions = _offPop;
        }
    }
}

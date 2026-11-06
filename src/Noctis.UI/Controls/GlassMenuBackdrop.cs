using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;

namespace Noctis.Controls;

/// <summary>
/// What a glass menu card frosts when it opens as its own native popup window (GitHub #169:
/// menus, drop-downs and tooltips of the mini player, the dialogs and plugin windows).
///
/// A <see cref="GlassPanel"/> blurs only the surface it renders into, and a native popup's
/// surface holds nothing but the card. So this decorator, laid under the card's GlassPanel,
/// paints that surface first: an opaque fill of the card's colour, then a quarter-size copy
/// of the window the popup belongs to (<see cref="BackdropSnapshot"/>, taken once per open on
/// the render thread), placed exactly where that window sits behind the card. The GlassPanel
/// then frosts it like the main window's menus frost the app. Where the card reaches past
/// the window there is only the fill: the card reads as the plain menu there.
///
/// Inert while the card is hosted in its window's overlay layer (the main window): the
/// GlassPanel sees the window itself. Nothing runs while the card is closed; one copy per
/// window is kept for a moment (<see cref="ReuseMs"/>) so tooltips hovered one after another
/// do not each take a snapshot.
/// </summary>
public sealed class GlassMenuBackdrop : Decorator
{
    /// <summary>The card's colour: the fill under the window copy, made opaque.</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<GlassMenuBackdrop>();

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        Border.CornerRadiusProperty.AddOwner<GlassMenuBackdrop>();

    /// <summary>0..1 fade of this backdrop's own drawing (the card's open/close motion). Not
    /// Opacity: that would fade the glass and rows above it a second time.</summary>
    public static readonly StyledProperty<double> FadeProperty =
        AvaloniaProperty.Register<GlassMenuBackdrop, double>(nameof(Fade), 1.0);

    /// <summary>A window copy younger than this is reused by the next card that opens over
    /// the same window at the same size.</summary>
    internal const int ReuseMs = 2000;

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public double Fade
    {
        get => GetValue(FadeProperty);
        set => SetValue(FadeProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    static GlassMenuBackdrop()
    {
        AffectsRender<GlassMenuBackdrop>(BackgroundProperty, CornerRadiusProperty, FadeProperty);
    }

    private Window? _window;
    private Shot? _shot;
    private readonly List<Visual> _watched = new();

    /// <summary>The window whose copy this backdrop paints; null while inert.</summary>
    internal Window? Window => _window;

    /// <summary>The copy painted, once taken.</summary>
    internal Bitmap? Bitmap => _shot?.Bitmap;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    /// <summary>The window a native popup belongs to (popups of popups included).</summary>
    internal static Window? OwnerWindow(TopLevel? top)
    {
        while (top is PopupRoot popup) top = popup.ParentTopLevel;
        return top as Window;
    }

    private void Start()
    {
        Stop();
        if (!AppGlass.IsActive) return;
        // In a window's overlay layer the GlassPanel above frosts the window itself.
        if (TopLevel.GetTopLevel(this) is not PopupRoot root || OwnerWindow(root) is not { } window) return;
        _window = window;
        _shot = Shot.Acquire(window, BaseColor());
        if (_shot.Bitmap is null) _shot.Ready += OnShotReady;

        // The card's open/close motion moves it with a RenderTransform, which re-renders
        // nothing: the copy is re-placed whenever a transform between here and the popup root
        // changes, so it stays pinned to the window behind.
        for (Visual? v = this; v != null && v is not TopLevel; v = v.GetVisualParent())
        {
            v.PropertyChanged += OnAncestorPropertyChanged;
            _watched.Add(v);
        }
        InvalidateVisual();
    }

    private void Stop()
    {
        foreach (var v in _watched) v.PropertyChanged -= OnAncestorPropertyChanged;
        _watched.Clear();
        if (_shot != null)
        {
            _shot.Ready -= OnShotReady;
            Shot.Release(_shot);
            _shot = null;
        }
        _window = null;
    }

    private void OnShotReady() => InvalidateVisual();

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RenderTransformProperty) InvalidateVisual();
    }

    private Color BaseColor() => Background is ISolidColorBrush solid
        ? Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B)
        : Colors.Black;

    /// <summary>Where the window's client area sits in this control's coordinates (every
    /// transform on the way included), or null when either side is gone.</summary>
    internal Rect? WindowRect()
    {
        if (_window is not { } window || TopLevel.GetTopLevel(this) is null) return null;
        var size = window.ClientSize;
        var topLeft = this.PointToClient(window.PointToScreen(default));
        var bottomRight = this.PointToClient(window.PointToScreen(new Point(size.Width, size.Height)));
        return new Rect(topLeft, bottomRight);
    }

    /// <summary>The popups this card's popup opened from (a tooltip or list inside a flyout, a
    /// submenu's parent menu), in this control's coordinates: they sit between the card and
    /// the window, so the window copy must not show where they are. Without this a tooltip over
    /// the Edit Info artwork chooser frosted the editor behind the chooser card, as if the
    /// tooltip were a hole into the back.</summary>
    internal List<Rect> ParentPopupRects()
    {
        var rects = new List<Rect>();
        if (_window is null || TopLevel.GetTopLevel(this) is not PopupRoot root) return rects;
        for (var top = root.ParentTopLevel; top is PopupRoot parent; top = parent.ParentTopLevel)
        {
            var size = parent.ClientSize;
            var topLeft = this.PointToClient(parent.PointToScreen(default));
            var bottomRight = this.PointToClient(parent.PointToScreen(new Point(size.Width, size.Height)));
            rects.Add(new Rect(topLeft, bottomRight));
        }
        return rects;
    }

    public override void Render(DrawingContext context)
    {
        if (_window is null || Fade <= 0) return;
        var bounds = new Rect(Bounds.Size);
        var shape = new RoundedRect(bounds, CornerRadius);
        using var fade = context.PushOpacity(Math.Min(Fade, 1));
        // Opaque: nothing behind the popup window may show through the glass tint (GitHub #81).
        context.DrawRectangle(new ImmutableSolidColorBrush(BaseColor()), null, shape);
        if (_shot?.Bitmap is not { } bitmap || WindowRect() is not { } rect) return;
        using (context.PushClip(shape))
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
            // Where a parent popup covers the window, the card reads as the plain card.
            var fill = new ImmutableSolidColorBrush(BaseColor());
            foreach (var covered in ParentPopupRects())
                context.DrawRectangle(fill, null, covered);
        }
    }

    /// <summary>One window copy, shared by the cards open over it.</summary>
    private sealed class Shot
    {
        private static Shot? _last;
        private static bool _hooked;

        private readonly WeakReference<Window> _window;
        private readonly Size _size;
        private readonly Color _base;
        private readonly long _taken = Environment.TickCount64;
        private int _users;

        public Bitmap? Bitmap { get; private set; }
        public event Action? Ready;

        private Shot(Window window, Color baseColor)
        {
            _window = new WeakReference<Window>(window);
            _size = window.ClientSize;
            _base = baseColor;
        }

        public static Shot Acquire(Window window, Color baseColor)
        {
            if (!_hooked)
            {
                _hooked = true;
                AppGlass.Changed += (_, _) => { if (!AppGlass.IsActive) Drop(); };
            }
            if (_last is { } last && last._window.TryGetTarget(out var w) && ReferenceEquals(w, window)
                && last._size == window.ClientSize && last._base == baseColor
                && Environment.TickCount64 - last._taken < ReuseMs)
            {
                last._users++;
                return last;
            }
            Drop();
            var shot = new Shot(window, baseColor) { _users = 1 };
            _last = shot;
            window.Closed += shot.OnWindowClosed;
            _ = shot.TakeAsync(window);
            return shot;
        }

        public static void Release(Shot shot)
        {
            shot._users--;
            if (shot._users <= 0 && !ReferenceEquals(shot, _last)) shot.Free();
        }

        /// <summary>Lets go of the kept copy (freed now if no card shows it).</summary>
        private static void Drop()
        {
            if (_last is not { } last) return;
            _last = null;
            if (last._users <= 0) last.Free();
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(this, _last)) Drop();
        }

        private async System.Threading.Tasks.Task TakeAsync(Window window)
        {
            var source = await BackdropSnapshot.CaptureSourceAsync(window, _base, "GlassMenuBackdrop");
            if (source is null || _freed) return;
            Bitmap = source.CreateBitmap(source.Pixels);
            Ready?.Invoke();
        }

        private bool _freed;

        private void Free()
        {
            if (_freed) return;
            _freed = true;
            if (_window.TryGetTarget(out var window)) window.Closed -= OnWindowClosed;
            // The last frame drawn may still use it: freed a beat later.
            if (Bitmap is { } bitmap) Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
            Bitmap = null;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// Cover Flow for a list of albums (GitHub #149): the library's albums on the Albums page,
/// one artist's on the artist page. One album sits flat in the middle with its title and
/// artist underneath; the rest stand turned towards it on both sides
/// (<see cref="AlbumCarouselGeometry"/>). Wheel, ← → (Home / End / Page Up / Page Down) and
/// dragging browse; clicking a side album brings it to the middle; clicking the middle one
/// or pressing Enter runs <see cref="OpenCommand"/> with it (the album page — what a click on
/// a grid tile does).
///
/// Built for big libraries: only the cards near the centre exist (a small recycled pool,
/// sized from the width), and the row glides with an exponential follow
/// (<see cref="AlbumCarouselGeometry.Approach"/>) on animation frames that stop when it rests.
/// Separate from the now-playing Cover Flow page (CoverFlowView), which is queue-driven.
/// </summary>
public sealed class AlbumCarousel : Panel
{
    public static readonly StyledProperty<IEnumerable?> AlbumsProperty =
        AvaloniaProperty.Register<AlbumCarousel, IEnumerable?>(nameof(Albums));

    public static readonly StyledProperty<AlbumCarouselStyle> CaseStyleProperty =
        AvaloniaProperty.Register<AlbumCarousel, AlbumCarouselStyle>(nameof(CaseStyle));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<AlbumCarousel, ICommand?>(nameof(OpenCommand));

    /// <summary>The centred album (two-way): the host can remember it and hand it back so the
    /// row comes back where it was, e.g. after visiting the album's page.</summary>
    public static readonly StyledProperty<Album?> CenterAlbumProperty =
        AvaloniaProperty.Register<AlbumCarousel, Album?>(nameof(CenterAlbum),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Take the keyboard when shown, so ← → work at once. Off where the carousel
    /// sits inside a scrolling page (the artist page): focusing it there could scroll the page.</summary>
    public static readonly StyledProperty<bool> FocusOnShowProperty =
        AvaloniaProperty.Register<AlbumCarousel, bool>(nameof(FocusOnShow), true);

    public bool FocusOnShow { get => GetValue(FocusOnShowProperty); set => SetValue(FocusOnShowProperty, value); }
    public IEnumerable? Albums { get => GetValue(AlbumsProperty); set => SetValue(AlbumsProperty, value); }
    public AlbumCarouselStyle CaseStyle { get => GetValue(CaseStyleProperty); set => SetValue(CaseStyleProperty, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public Album? CenterAlbum { get => GetValue(CenterAlbumProperty); set => SetValue(CenterAlbumProperty, value); }

    /// <summary>Height kept under the cards for the caption.</summary>
    internal const double CaptionHeight = 74;

    /// <summary>Movement past this many px turns a press into a drag (no click).</summary>
    private const double DragThreshold = 6;

    private IReadOnlyList<Album> _items = Array.Empty<Album>();
    private INotifyCollectionChanged? _observed;

    private double _position;
    private int _target;
    private bool _frameQueued;
    private long _lastFrameTicks;

    private readonly Dictionary<int, AlbumCarouselCard> _realized = new();
    private readonly Stack<AlbumCarouselCard> _pool = new();
    private readonly Canvas _cardLayer = new() { ClipToBounds = false };
    private readonly StackPanel _caption;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _counter;

    private double _coverSize = 240;
    private int _sides = 6;
    private int _captionIndex = -1;
    private bool _syncingCenter;

    // Pointer state
    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private double _pressPosition;
    private AlbumCarouselCard? _pressCard;
    private double _velocity;
    private long _lastMoveTicks;
    private double _lastMovePosition;
    private double _wheelAccum;

    public AlbumCarousel()
    {
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent; // wheel / drag anywhere on the strip
        Avalonia.Automation.AutomationProperties.SetName(this, Loc.T("AlbumCarousel.Name"));

        _title = new TextBlock
        {
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxLines = 1,
        };
        _subtitle = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxLines = 1,
        };
        _counter = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.35,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _caption = new StackPanel
        {
            Spacing = 2,
            IsHitTestVisible = false,
            Children = { _title, _subtitle, _counter },
        };
        Children.Add(_cardLayer);
        Children.Add(_caption);
    }

    // ── Public surface for the host and tests ─────────────────────────────────

    /// <summary>Index of the album the row is heading to (or resting on).</summary>
    public int TargetIndex => _target;

    /// <summary>Current (possibly fractional, mid-glide) position of the row.</summary>
    internal double Position => _position;

    /// <summary>Cards currently built (virtualization check).</summary>
    internal int RealizedCount => _realized.Count;

    internal IReadOnlyDictionary<int, AlbumCarouselCard> RealizedCards => _realized;

    internal string CaptionTitle => _title.Text ?? "";

    /// <summary>Glide to album <paramref name="index"/> (clamped).</summary>
    public void MoveTo(int index)
    {
        if (_items.Count == 0) return;
        index = Math.Clamp(index, 0, _items.Count - 1);
        if (index == _target && Math.Abs(_position - index) < 0.0001) return;
        _target = index;
        PublishCenter();
        QueueFrame();
    }

    /// <summary>Jump to album <paramref name="index"/> without the glide.</summary>
    public void SnapTo(int index)
    {
        if (_items.Count == 0) return;
        _target = Math.Clamp(index, 0, _items.Count - 1);
        _position = _target;
        PublishCenter();
        Refresh();
    }

    /// <summary>Open the centred album (Enter / click on it).</summary>
    public void OpenCenter()
    {
        if (_target < 0 || _target >= _items.Count) return;
        var album = _items[_target];
        if (OpenCommand?.CanExecute(album) == true) OpenCommand.Execute(album);
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AlbumsProperty)
        {
            if (_observed != null) _observed.CollectionChanged -= OnItemsChanged;
            _observed = change.NewValue as INotifyCollectionChanged;
            if (_observed != null) _observed.CollectionChanged += OnItemsChanged;
            ReloadItems();
        }
        else if (change.Property == CaseStyleProperty)
        {
            foreach (var card in _realized.Values) card.CaseStyle = CaseStyle;
            foreach (var card in _pool) card.CaseStyle = CaseStyle;
            InvalidateArrange();
        }
        else if (change.Property == CenterAlbumProperty && !_syncingCenter)
        {
            if (change.NewValue is Album album)
            {
                var i = IndexOf(album);
                if (i >= 0 && i != _target) SnapTo(i);
            }
        }
        else if (change.Property == IsVisibleProperty && IsVisible)
        {
            FocusSoon();
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ReloadItems();

    /// <summary>Re-reads the list, keeping the centred album centred when it is still there
    /// (a search, a re-sort or an artist page filling in batches must not throw the row back
    /// to the first album).</summary>
    private void ReloadItems()
    {
        var keep = _target >= 0 && _target < _items.Count ? _items[_target] : CenterAlbum;
        _items = Albums switch
        {
            null => Array.Empty<Album>(),
            IReadOnlyList<Album> list => list.ToList(),
            _ => Albums.OfType<Album>().ToList(),
        };

        // Cards still showing the same album at the same index stay as they are (no cover
        // reload when, say, the artist page appends its next batch); the rest are recycled.
        List<int>? stale = null;
        foreach (var (index, card) in _realized)
            if (index >= _items.Count || !ReferenceEquals(_items[index], card.Album)) (stale ??= new()).Add(index);
        if (stale != null)
            foreach (var index in stale)
            {
                Recycle(_realized[index]);
                _realized.Remove(index);
            }
        _captionIndex = -1;

        if (_items.Count == 0)
        {
            _target = 0;
            _position = 0;
            UpdateCaption();
            return;
        }

        var at = keep != null ? IndexOf(keep) : -1;
        if (at < 0 && CenterAlbum != null) at = IndexOf(CenterAlbum);
        _target = Math.Clamp(at < 0 ? 0 : at, 0, _items.Count - 1);
        _position = _target;
        PublishCenter();
        Refresh();
    }

    private int IndexOf(Album album)
    {
        for (var i = 0; i < _items.Count; i++)
            if (ReferenceEquals(_items[i], album)) return i;
        // Albums are rebuilt on a library refresh: fall back to the stable id.
        for (var i = 0; i < _items.Count; i++)
            if (_items[i].Id == album.Id) return i;
        return -1;
    }

    private void PublishCenter()
    {
        if (_items.Count == 0) return;
        _syncingCenter = true;
        try { CenterAlbum = _items[_target]; }
        finally { _syncingCenter = false; }
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        _caption.Measure(new Size(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, CaptionHeight));
        _cardLayer.Measure(Size.Infinity);
        var w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cardArea = Math.Max(0, finalSize.Height - CaptionHeight);
        var factor = AlbumCarouselCard.SizeFactor(CaseStyle);
        var reflection = AlbumCarouselCard.ReflectionFactor(CaseStyle);
        // The centred card takes ~70% of the strip's height (reflection included) and at
        // most ~38% of its width, so a few covers always show on each side.
        var byHeight = cardArea * 0.72 / (factor.Height + reflection);
        var byWidth = finalSize.Width * 0.38 / factor.Width;
        var cover = Math.Clamp(Math.Min(byHeight, byWidth), 90, 460);
        if (Math.Abs(cover - _coverSize) > 0.5) _coverSize = cover;
        _sides = AlbumCarouselGeometry.VisibleSides(finalSize.Width, factor.Width * _coverSize);

        _cardLayer.Arrange(new Rect(0, 0, finalSize.Width, cardArea));
        var captionWidth = Math.Min(finalSize.Width - 32, Math.Max(240, factor.Width * _coverSize * 1.6));
        _caption.Arrange(new Rect((finalSize.Width - captionWidth) / 2, cardArea, Math.Max(0, captionWidth), CaptionHeight));
        Refresh();
        return finalSize;
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    /// <summary>Builds the cards in reach of the current position, recycles the rest, and
    /// poses every built card. Cheap: a few dozen controls whatever the library size.</summary>
    private void Refresh()
    {
        var count = _items.Count;
        var size = _cardLayer.Bounds.Size;
        if (count == 0 || size.Width <= 0)
        {
            UpdateCaption();
            return;
        }

        var lo = Math.Max(0, (int)Math.Floor(_position) - _sides - 1);
        var hi = Math.Min(count - 1, (int)Math.Ceiling(_position) + _sides + 1);

        List<int>? stale = null;
        foreach (var index in _realized.Keys)
            if (index < lo || index > hi) (stale ??= new()).Add(index);
        if (stale != null)
            foreach (var index in stale)
            {
                Recycle(_realized[index]);
                _realized.Remove(index);
            }

        var factor = AlbumCarouselCard.SizeFactor(CaseStyle);
        var cardW = factor.Width * _coverSize;
        var cardH = factor.Height * _coverSize;
        // A record's SLEEVE is centred, not the card: the disc peeking out hangs off to the
        // right instead of pushing the sleeve off-centre.
        var coverCentre = CoverCentreX(CaseStyle, _coverSize);
        var left = size.Width / 2 - coverCentre;
        // Cards sit a little above the strip's middle so the cover style's reflection fits.
        var top = (size.Height - cardH - AlbumCarouselCard.ReflectionFactor(CaseStyle) * _coverSize) / 2;
        var depth = AlbumCarouselGeometry.Depth(cardW);

        for (var i = lo; i <= hi; i++)
        {
            if (!_realized.TryGetValue(i, out var card))
            {
                card = Rent();
                card.Index = i;
                card.Album = _items[i];
                _realized[i] = card;
            }
            card.CaseStyle = CaseStyle;
            card.CoverSize = _coverSize;
            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);

            var offset = i - _position;
            var pose = AlbumCarouselGeometry.At(offset, cardW, _sides);
            var (rotate, scale, move) = Transforms(card);
            rotate.Depth = depth;
            rotate.AngleY = pose.AngleY;
            scale.ScaleX = pose.Scale;
            scale.ScaleY = pose.Scale;
            move.X = pose.X;
            card.Opacity = pose.Opacity;
            card.IsVisible = pose.Opacity > 0.001;
            card.ZIndex = AlbumCarouselGeometry.ZIndexAt(offset);
            card.Dim = Math.Min(1, Math.Abs(offset)) * 0.28;
        }

        UpdateCaption();
    }

    private static double CoverCentreX(AlbumCarouselStyle style, double cover) => style switch
    {
        AlbumCarouselStyle.Vinyl => cover / 2,
        _ => AlbumCarouselCard.SizeFactor(style).Width * cover / 2,
    };

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private AlbumCarouselCard Rent()
    {
        if (_pool.Count > 0)
        {
            var reused = _pool.Pop();
            reused.IsVisible = true;
            return reused;
        }
        var card = new AlbumCarouselCard
        {
            CaseStyle = CaseStyle,
            CoverSize = _coverSize,
            Cursor = HandCursor,
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        };
        var group = new TransformGroup();
        group.Children.Add(new Rotate3DTransform());
        group.Children.Add(new ScaleTransform());
        group.Children.Add(new TranslateTransform());
        card.RenderTransform = group;
        _cardLayer.Children.Add(card);
        return card;
    }

    private void Recycle(AlbumCarouselCard card)
    {
        card.IsVisible = false;
        card.Index = -1;
        card.Album = null;
        _pool.Push(card);
    }

    private static (Rotate3DTransform Rotate, ScaleTransform Scale, TranslateTransform Move) Transforms(AlbumCarouselCard card)
    {
        var group = (TransformGroup)card.RenderTransform!;
        return ((Rotate3DTransform)group.Children[0], (ScaleTransform)group.Children[1], (TranslateTransform)group.Children[2]);
    }

    private void UpdateCaption()
    {
        var index = _items.Count == 0 ? -1 : Math.Clamp((int)Math.Round(_position), 0, _items.Count - 1);
        if (index == _captionIndex) return;
        _captionIndex = index;
        if (index < 0)
        {
            _title.Text = _subtitle.Text = _counter.Text = string.Empty;
            return;
        }
        var album = _items[index];
        _title.Text = album.Name;
        _subtitle.Text = album.TileSubtitle;
        _counter.Text = Loc.T("AlbumCarousel.Counter", index + 1, _items.Count);
    }

    // ── Glide ─────────────────────────────────────────────────────────────────

    private void QueueFrame()
    {
        if (_frameQueued) return;
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            // Not on screen: no frames to wait for, land at once.
            _position = _target;
            Refresh();
            return;
        }
        _frameQueued = true;
        _lastFrameTicks = Stopwatch.GetTimestamp();
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameQueued = false;
        if (_dragging) return;
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Min(0.05, (now - _lastFrameTicks) / (double)Stopwatch.Frequency);
        _lastFrameTicks = now;
        StepGlide(dt);
        if (_position != _target) QueueFrame();
    }

    /// <summary>Advances the glide by <paramref name="dt"/> seconds. Internal so tests can step it.</summary>
    internal void StepGlide(double dt)
    {
        _position = AlbumCarouselGeometry.Approach(_position, _target, dt);
        Refresh();
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_items.Count == 0) return;
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta == 0) return;
        e.Handled = true;
        // A notch is one album; a trackpad's small deltas add up to one. Wheel down /
        // swipe left (negative) moves on, like the Cover Flow page.
        _wheelAccum += delta;
        var steps = (int)Math.Truncate(_wheelAccum);
        if (steps == 0) return;
        _wheelAccum -= steps;
        MoveTo(_target - steps);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _items.Count == 0 || e.KeyModifiers != KeyModifiers.None) return;
        var page = Math.Max(1, _sides);
        switch (e.Key)
        {
            case Key.Left: MoveTo(_target - 1); break;
            case Key.Right: MoveTo(_target + 1); break;
            case Key.PageUp: MoveTo(_target - page); break;
            case Key.PageDown: MoveTo(_target + page); break;
            case Key.Home: MoveTo(0); break;
            case Key.End: MoveTo(_items.Count - 1); break;
            case Key.Enter: OpenCenter(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (_items.Count == 0) return;
        _pressed = true;
        _dragging = false;
        _pressPoint = e.GetPosition(this);
        _pressPosition = _position;
        _pressCard = CardFrom(e.Source);
        _velocity = 0;
        _lastMoveTicks = Stopwatch.GetTimestamp();
        _lastMovePosition = _position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed) return;
        var dx = e.GetPosition(this).X - _pressPoint.X;
        if (!_dragging && Math.Abs(dx) < DragThreshold) return;
        _dragging = true;
        var cardW = AlbumCarouselCard.SizeFactor(CaseStyle).Width * _coverSize;
        var pos = _pressPosition - dx / AlbumCarouselGeometry.DragPixelsPerAlbum(cardW);
        _position = Math.Clamp(pos, -0.45, _items.Count - 0.55);

        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastMoveTicks) / (double)Stopwatch.Frequency;
        if (dt > 0.004)
        {
            var v = (_position - _lastMovePosition) / dt;
            _velocity = _velocity * 0.6 + v * 0.4;
            _lastMoveTicks = now;
            _lastMovePosition = _position;
        }
        Refresh();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        if (_dragging)
        {
            _dragging = false;
            // A drag that stopped before release carries no flick.
            var idle = (Stopwatch.GetTimestamp() - _lastMoveTicks) / (double)Stopwatch.Frequency;
            var velocity = idle > 0.08 ? 0 : _velocity;
            _target = AlbumCarouselGeometry.SnapTarget(_position, velocity, _items.Count);
            PublishCenter();
            QueueFrame();
            return;
        }
        Click(_pressCard);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_pressed) return;
        _pressed = false;
        if (_dragging)
        {
            _dragging = false;
            _target = AlbumCarouselGeometry.SnapTarget(_position, 0, _items.Count);
            PublishCenter();
            QueueFrame();
        }
    }

    /// <summary>A click (no drag) on <paramref name="card"/>: the centred album opens, a side
    /// album comes to the middle. Internal so tests can click without a pointer.</summary>
    internal void Click(AlbumCarouselCard? card)
    {
        if (card == null || card.Index < 0) return;
        if (card.Index == _target && Math.Abs(_position - _target) < 0.5) OpenCenter();
        else MoveTo(card.Index);
    }

    private static AlbumCarouselCard? CardFrom(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is AlbumCarouselCard card) return card;
        return null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsVisible) FocusSoon();
    }

    /// <summary>Take the keyboard once shown so ← → work without a click first — unless the
    /// user is typing somewhere (the top-bar search).</summary>
    private void FocusSoon() => Dispatcher.UIThread.Post(() =>
    {
        if (!FocusOnShow || !IsVisible || TopLevel.GetTopLevel(this) is not { } top) return;
        if (top.FocusManager?.GetFocusedElement() is TextBox) return;
        Focus();
    }, DispatcherPriority.Background);
}

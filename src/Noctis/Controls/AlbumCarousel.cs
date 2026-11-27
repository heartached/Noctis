using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

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
///
/// GitHub #233: an album can wear its own case (<see cref="AlbumCarouselStyleOverrides"/>), so
/// one row may mix covers, jewel cases, records and cassettes: every card stands on the same
/// floor line with its own size, and the row spaces itself by the centred card. A soft glow
/// in the centred cover's colour sits behind it (<see cref="GlowEnabled"/>): one radial
/// gradient ellipse, no blur effect, cross-fading between neighbours as the row glides.
/// </summary>
public sealed class AlbumCarousel : Panel
{
    public static readonly StyledProperty<IEnumerable?> AlbumsProperty =
        AvaloniaProperty.Register<AlbumCarousel, IEnumerable?>(nameof(Albums));

    public static readonly StyledProperty<AlbumCarouselStyle> CaseStyleProperty =
        AvaloniaProperty.Register<AlbumCarousel, AlbumCarouselStyle>(nameof(CaseStyle));

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<AlbumCarousel, ICommand?>(nameof(OpenCommand));

    /// <summary>Runs with the artist name clicked in the caption (the album tiles' artist link,
    /// <see cref="CaptionCreditLink"/>). Unset, the artist line is plain text.</summary>
    public static readonly StyledProperty<ICommand?> ArtistCommandProperty =
        AvaloniaProperty.Register<AlbumCarousel, ICommand?>(nameof(ArtistCommand));

    /// <summary>The centred album (two-way): the host can remember it and hand it back so the
    /// row comes back where it was, e.g. after visiting the album's page.</summary>
    public static readonly StyledProperty<Album?> CenterAlbumProperty =
        AvaloniaProperty.Register<AlbumCarousel, Album?>(nameof(CenterAlbum),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Take the keyboard when shown, so ← → work at once. Off where the carousel
    /// sits inside a scrolling page (the artist page): focusing it there could scroll the page.</summary>
    public static readonly StyledProperty<bool> FocusOnShowProperty =
        AvaloniaProperty.Register<AlbumCarousel, bool>(nameof(FocusOnShow), true);

    /// <summary>GitHub #244: the row runs on to the window's left and right edges (under the
    /// sidebar rail and past the page's margins) instead of stopping at the strip's own
    /// bounds. Layout is unchanged: the row stays centred on the strip. Only where the
    /// carousel fills a page (the Albums page); a scrolling page (the artist page) must
    /// keep its clips.</summary>
    public static readonly StyledProperty<bool> BleedToWindowEdgesProperty =
        AvaloniaProperty.Register<AlbumCarousel, bool>(nameof(BleedToWindowEdges));

    public bool BleedToWindowEdges { get => GetValue(BleedToWindowEdgesProperty); set => SetValue(BleedToWindowEdgesProperty, value); }
    public bool FocusOnShow { get => GetValue(FocusOnShowProperty); set => SetValue(FocusOnShowProperty, value); }
    public IEnumerable? Albums { get => GetValue(AlbumsProperty); set => SetValue(AlbumsProperty, value); }
    public AlbumCarouselStyle CaseStyle { get => GetValue(CaseStyleProperty); set => SetValue(CaseStyleProperty, value); }
    public ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? ArtistCommand { get => GetValue(ArtistCommandProperty); set => SetValue(ArtistCommandProperty, value); }
    public Album? CenterAlbum { get => GetValue(CenterAlbumProperty); set => SetValue(CenterAlbumProperty, value); }

    /// <summary>Height kept under the cards for the caption.</summary>
    internal const double CaptionHeight = 98;

    /// <summary>The album page's quality-badge waves (AlbumDetailView), same path.</summary>
    private const string QualityWavesPath = "M8.184,0.35C9.944,0.35 10.703,3.296 11.338,5.238C11.673,3.842 11.497,3.542 11.857,3.542C11.99,3.542 12.126,3.633 12.126,3.798C12.126,3.809 12.123,3.839 12.117,3.883L12.091,4.058C12.02,4.522 11.845,5.494 11.654,6.144C13.198,10.191 14.345,4.861 14.474,3.772C14.493,3.615 14.612,3.542 14.731,3.542C14.891,3.542 15.022,3.662 14.997,3.843C14.72,5.605 14.295,8.35 12.547,8.35C11.582,8.35 11.04,7.595 10.611,6.73C9.54,4.626 9.047,1.093 7.997,1.093C7.66,1.093 7.411,1.444 7.394,1.444C7.362,1.444 7.337,1.301 7.023,0.909C7.322,0.567 7.734,0.35 8.184,0.35ZM2.458,0.354C5.211,0.354 5.456,7.618 7.014,7.618C7.197,7.618 7.394,7.507 7.61,7.256C7.729,7.458 7.851,7.638 7.978,7.796C7.667,8.151 7.28,8.35 6.795,8.35C5.054,8.349 4.306,5.434 3.663,3.466C3.511,4.097 3.432,4.669 3.402,4.925C3.382,5.088 3.263,5.163 3.143,5.163C3.009,5.163 2.874,5.071 2.874,4.908L2.874,4.908L2.877,4.87C2.966,4.223 3.146,3.243 3.347,2.56C3.079,1.858 2.745,1.091 2.252,1.091C1.257,1.091 0.687,3.591 0.527,4.925C0.508,5.088 0.388,5.163 0.268,5.163C0.135,5.163 0,5.071 0,4.908C0,4.896 0.001,4.883 0.002,4.87C0.283,2.836 0.808,0.354 2.458,0.354ZM5.315,0.35C5.809,0.35 6.339,0.608 6.797,1.211C6.822,1.241 7.078,1.639 7.159,1.777C8.277,3.802 8.818,7.627 9.881,7.627C10.065,7.627 10.264,7.513 10.484,7.256C10.604,7.458 10.726,7.638 10.852,7.796C10.542,8.15 10.155,8.35 9.67,8.35C6.933,8.349 6.636,1.09 5.128,1.09C4.788,1.09 4.536,1.444 4.519,1.444C4.487,1.444 4.462,1.301 4.148,0.909C4.455,0.558 4.87,0.35 5.315,0.35Z";

    private static readonly Avalonia.Data.Converters.IMultiValueConverter MetadataLine = new Noctis.Converters.MetadataLineConverter();

    /// <summary>Movement past this many px turns a press into a drag (no click).</summary>
    private const double DragThreshold = 6;

    private IReadOnlyList<Album> _items = Array.Empty<Album>();
    private INotifyCollectionChanged? _observed;

    private double _position;
    private int _target;
    private bool _frameQueued;
    private long _lastFrameTicks;

    private readonly Dictionary<int, AlbumCarouselCard> _realized = new();
    private readonly GlowLayer _glow = new() { IsHitTestVisible = false };
    private IReadOnlyDictionary<Guid, AlbumCarouselStyle> _styleOverrides = new Dictionary<Guid, AlbumCarouselStyle>();
    private bool _glowEnabled = true;
    /// <summary>Glow colour per artwork path: null = the cover is grey or unreadable (accent).</summary>
    private readonly Dictionary<string, Color?> _glowColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _glowPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<AlbumCarouselCard> _pool = new();
    private readonly Canvas _cardLayer = new() { ClipToBounds = false };
    private readonly BackdropBleed _bleed;
    private double _reachLeft;
    private double _reachRight;
    private readonly StackPanel _caption;
    private readonly TextBlock _title;
    private readonly TextBlock _qualityTipTitle;
    private readonly TextBlock _qualityTipInfo;
    private readonly TextBlock _qualityTipDescription;
    private readonly Border _explicit;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _details;
    private readonly TextBlock _qualityDot;
    private readonly StackPanel _quality;
    private readonly TextBlock _qualityText;
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

    /// <summary>Right-click / menu key on a card (GitHub #178: the album menu, with Group into One
    /// Album, works in the carousel too). Sender is the card; the album is the argument.</summary>
    public event EventHandler<Album>? AlbumContextRequested;

    public AlbumCarousel()
    {
        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            // The pressed card, else the centred one (menu key on the focused strip).
            var card = CardFrom(e.Source);
            var album = card?.Album ?? CenterAlbum;
            if (album == null || AlbumContextRequested == null) return;
            AlbumContextRequested.Invoke((object?)card ?? this, album);
            e.Handled = true;
        });
        Focusable = true;
        ClipToBounds = true;
        // GitHub #244: measures the strip's distance to the window's edges and lifts the page
        // hosts' clips while BleedToWindowEdges is on; ApplyReach draws that far.
        _bleed = new BackdropBleed(this) { Changed = ApplyReach };
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
            VerticalAlignment = VerticalAlignment.Center,
        };
        // "E" beside the title, as on the album page (global explicit-badge styles).
        _explicit = new Border
        {
            Classes = { "explicit-badge" },
            Margin = new Thickness(8, 0, 0, 0),
            IsVisible = false,
            Child = new TextBlock { Text = "E", FontSize = 10, Opacity = 0.95, Classes = { "explicit-badge-text" } },
        };
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
            Children = { _title, _explicit },
        };
        // Artist in the accent colour, like the album page's artist links.
        _subtitle = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxLines = 1,
        };
        _subtitle.Bind(TextBlock.ForegroundProperty, _subtitle.GetResourceObservable("AccentTextExactBrush"));
        // Each name opens its artist, as on an album tile (owner 11-27).
        _subtitle.Bind(CaptionCreditLink.CommandProperty, this.GetObservable(ArtistCommandProperty));
        // Third row: genre · release date · tracks · length · quality badge (album page line).
        _details = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        _qualityDot = new TextBlock { Text = " · ", FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        _qualityText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        var waves = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(QualityWavesPath) };
        waves[!Shape.FillProperty] = _qualityText[!TextBlock.ForegroundProperty];
        // The album page's quality pop-up (owner 11-27: hovering "Lossless" here said nothing).
        _qualityTipTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold };
        _qualityTipInfo = new TextBlock { FontSize = 12, Opacity = 0.9 };
        _qualityTipDescription = new TextBlock { FontSize = 11, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        _quality = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, // hovered anywhere on the badge, not only on its glyphs
            Children =
            {
                new Viewbox { Width = 15, Height = 10, Opacity = 0.5, Margin = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = waves },
                _qualityText,
            },
        };
        ToolTip.SetTip(_quality, new StackPanel { Width = 220, Spacing = 4, Children = { _qualityTipTitle, _qualityTipInfo, _qualityTipDescription } });
        _details.IsHitTestVisible = _qualityDot.IsHitTestVisible = false;
        var detailsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _details, _qualityDot, _quality },
        };
        _counter = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.35,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            IsHitTestVisible = false,
        };
        // Only the artist names and the quality badge take the pointer; the rest of the caption
        // stays part of the row (a drag started on the title still turns the carousel).
        _caption = new StackPanel
        {
            Spacing = 2,
            Children = { titleRow, _subtitle, detailsRow, _counter },
        };
        Children.Add(_glow);
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
    internal TextBlock CaptionArtist => _subtitle;
    internal Control CaptionQuality => _quality;

    /// <summary>Albums wearing their own style (GitHub #233). Filled from settings while the
    /// carousel is on screen; tests set it directly.</summary>
    internal IReadOnlyDictionary<Guid, AlbumCarouselStyle> StyleOverrides
    {
        get => _styleOverrides;
        set
        {
            _styleOverrides = value;
            InvalidateArrange();
            Refresh();
        }
    }

    /// <summary>"Glow on the selected album" (GitHub #233). From settings while on screen.</summary>
    internal bool GlowEnabled
    {
        get => _glowEnabled;
        set
        {
            if (_glowEnabled == value) return;
            _glowEnabled = value;
            UpdateGlow();
        }
    }

    /// <summary>The glow behind the centred album (tests read where and how bright it is).</summary>
    internal Rect GlowRect => _glow.Ellipse;
    internal double GlowOpacity => _glow.IsVisible ? _glow.Opacity : 0;
    internal Color GlowColor => _glow.Color;
    internal int GlowRenders => _glow.Renders;

    /// <summary>The style album <paramref name="album"/> wears: its own pick, else the global one.</summary>
    internal AlbumCarouselStyle StyleOf(Album album) =>
        _styleOverrides.TryGetValue(album.Id, out var own) ? own : CaseStyle;

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
            foreach (var card in _realized.Values) card.CaseStyle = card.Album is { } a ? StyleOf(a) : CaseStyle;
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
        else if (change.Property == IsVisibleProperty)
        {
            UpdateBleed();
            if (IsVisible) FocusSoon();
        }
        else if (change.Property == BleedToWindowEdgesProperty)
        {
            UpdateBleed();
        }
    }

    // ── Reach to the window edges (GitHub #244) ───────────────────────────────

    /// <summary>The extra width drawn left / right of the strip right now (0 when off).</summary>
    internal double ReachLeft => _reachLeft;
    internal double ReachRight => _reachRight;

    /// <summary>Hidden (grid view), the page's clips stay as they are.</summary>
    private void UpdateBleed() => _bleed.Enabled = BleedToWindowEdges && IsVisible;

    /// <summary>Clips the strip to its own height but to the window's width, so the side
    /// albums run on under the sidebar and to the right edge alike (the hosts' clips are lifted
    /// by the bleed). Off, the strip clips to its bounds as before.</summary>
    private void ApplyReach()
    {
        var left = _bleed.Enabled ? _bleed.AppliedLeft : 0;
        var right = left > 0 ? _bleed.AppliedRight : 0;
        if (left > 0)
            Clip = new RectangleGeometry(new Rect(-left, 0, Bounds.Width + left + right, Bounds.Height));
        else
            ClearValue(ClipProperty);
        if (left == _reachLeft && right == _reachRight) return;
        _reachLeft = left;
        _reachRight = right;
        // More of the row shows: build the cards that now land on screen.
        InvalidateArrange();
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
        // Reaching to the window edges (#244), the row shows as far as the farther edge.
        var reach = Math.Max(_reachLeft, _reachRight);
        _sides = AlbumCarouselGeometry.VisibleSides(finalSize.Width + 2 * reach, factor.Width * _coverSize);

        _cardLayer.Arrange(new Rect(0, 0, finalSize.Width, cardArea));
        _glow.Arrange(new Rect(0, 0, finalSize.Width, cardArea));
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

        var floor = FloorY(size.Height);
        // The row spaces itself by the centred card's width (cross-faded mid-glide), so a
        // record or a jewel case in the middle of a row of covers gets room to the sides.
        var cardW = CentredWidth();

        for (var i = lo; i <= hi; i++)
        {
            if (!_realized.TryGetValue(i, out var card))
            {
                card = Rent();
                card.Index = i;
                card.Album = _items[i];
                _realized[i] = card;
            }
            var style = StyleOf(_items[i]);
            var factor = AlbumCarouselCard.SizeFactor(style);
            card.CaseStyle = style;
            card.CoverSize = _coverSize;
            // A record's SLEEVE is centred, not the card: the disc peeking out hangs off to the
            // right instead of pushing the sleeve off-centre. Every card stands on the floor.
            Canvas.SetLeft(card, size.Width / 2 - CoverCentreX(style, _coverSize));
            Canvas.SetTop(card, floor - factor.Height * _coverSize);

            var offset = i - _position;
            var pose = AlbumCarouselGeometry.At(offset, cardW, _sides);
            var (rotate, scale, move) = Transforms(card);
            rotate.Depth = AlbumCarouselGeometry.Depth(factor.Width * _coverSize);
            rotate.AngleY = pose.AngleY;
            scale.ScaleX = pose.Scale;
            scale.ScaleY = pose.Scale;
            move.X = pose.X;
            card.Opacity = pose.Opacity;
            card.IsVisible = pose.Opacity > 0.001;
            card.ZIndex = AlbumCarouselGeometry.ZIndexAt(offset);
            card.Dim = Math.Min(1, Math.Abs(offset)) * 0.28;
        }

        UpdateGlow();
        UpdateCaption();
    }

    /// <summary>The floor line every card stands on: the global style's card centred (with
    /// its reflection) in the strip, as before mixed styles existed.</summary>
    private double FloorY(double stripHeight)
    {
        var global = AlbumCarouselCard.SizeFactor(CaseStyle);
        var cardH = global.Height * _coverSize;
        return (stripHeight - cardH - AlbumCarouselCard.ReflectionFactor(CaseStyle) * _coverSize) / 2 + cardH;
    }

    /// <summary>Card width at the centre: the centred album's, blended with the next one's mid-glide.</summary>
    private double CentredWidth()
    {
        var (a, b, t) = Neighbours();
        double W(int i) => i < 0 ? AlbumCarouselCard.SizeFactor(CaseStyle).Width * _coverSize
            : AlbumCarouselCard.SizeFactor(StyleOf(_items[i])).Width * _coverSize;
        return W(a) + (W(b) - W(a)) * t;
    }

    /// <summary>The two albums either side of the (fractional) position and how far between them.</summary>
    private (int A, int B, double T) Neighbours()
    {
        if (_items.Count == 0) return (-1, -1, 0);
        var p = Math.Clamp(_position, 0, _items.Count - 1);
        var a = (int)Math.Floor(p);
        var b = Math.Min(a + 1, _items.Count - 1);
        return (a, b, p - a);
    }

    // ── Glow (GitHub #233) ────────────────────────────────────────────────────

    /// <summary>Glow ellipse size as multiples of the centred card's size.</summary>
    internal const double GlowSpread = 1.7;

    /// <summary>Places, sizes and colours the glow for the current position: the centred
    /// card's, blended with its neighbour's mid-glide, dimmed while between two albums.</summary>
    private void UpdateGlow()
    {
        var size = _cardLayer.Bounds.Size;
        var (a, b, t) = Neighbours();
        if (!_glowEnabled || a < 0 || size.Width <= 0)
        {
            _glow.IsVisible = false;
            return;
        }
        var floor = FloorY(size.Height);
        Rect Around(int i)
        {
            var style = StyleOf(_items[i]);
            var f = AlbumCarouselCard.SizeFactor(style);
            var w = f.Width * _coverSize;
            var h = f.Height * _coverSize;
            var cx = size.Width / 2 - CoverCentreX(style, _coverSize) + w / 2;
            var cy = floor - h / 2;
            return new Rect(cx - w * GlowSpread / 2, cy - h * GlowSpread / 2, w * GlowSpread, h * GlowSpread);
        }
        var ra = Around(a);
        var rb = b == a ? ra : Around(b);
        var rect = new Rect(
            ra.X + (rb.X - ra.X) * t, ra.Y + (rb.Y - ra.Y) * t,
            ra.Width + (rb.Width - ra.Width) * t, ra.Height + (rb.Height - ra.Height) * t);
        var ca = GlowColorOf(_items[a]);
        var cb = b == a ? ca : GlowColorOf(_items[b]);
        var color = Color.FromRgb(
            (byte)Math.Round(ca.R + (cb.R - ca.R) * t),
            (byte)Math.Round(ca.G + (cb.G - ca.G) * t),
            (byte)Math.Round(ca.B + (cb.B - ca.B) * t));
        _glow.IsVisible = true;
        // Fully lit on an album, half way down between two (no glow on side albums).
        _glow.Opacity = 1 - 0.5 * Math.Sin(Math.PI * t);
        // The strip clips to its own height (and width, or the window's with #244's reach):
        // an ellipse reaching past that ended on a hard line along the clip (Discord: a cut
        // above the centred cover on the artist page). Squeezed to fit, it fades out to
        // nothing on every side; the centre stays on the card.
        var visible = new Rect(-_reachLeft, 0, size.Width + _reachLeft + _reachRight, size.Height + CaptionHeight);
        _glow.Set(FitGlow(rect, visible), color);
    }

    /// <summary><paramref name="glow"/> shrunk about its centre (each axis on its own) until it
    /// lies inside <paramref name="visible"/>.</summary>
    internal static Rect FitGlow(Rect glow, Rect visible)
    {
        var c = glow.Center;
        var rx = Math.Max(0, Math.Min(glow.Width / 2, Math.Min(c.X - visible.Left, visible.Right - c.X)));
        var ry = Math.Max(0, Math.Min(glow.Height / 2, Math.Min(c.Y - visible.Top, visible.Bottom - c.Y)));
        return new Rect(c.X - rx, c.Y - ry, rx * 2, ry * 2);
    }

    /// <summary>The centred cover's colour for the glow: the vibrant colour "Accent follows
    /// album art" uses (decoded off the UI thread, cached by path), else the accent.</summary>
    private Color GlowColorOf(Album album)
    {
        var path = album.ArtworkPath;
        if (!string.IsNullOrEmpty(path))
        {
            if (_glowColors.TryGetValue(path, out var known)) return known ?? AccentColor();
            if (_glowPending.Add(path))
            {
                System.Threading.Tasks.Task.Run(() => ArtworkVibrantColor.GetAccentHex(path)).ContinueWith(task =>
                {
                    Color? color = task.IsCompletedSuccessfully && task.Result is { } hex && Color.TryParse(hex, out var c) ? c : null;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _glowPending.Remove(path);
                        if (_glowColors.Count > 512) _glowColors.Clear();
                        _glowColors[path] = color;
                        UpdateGlow();
                    });
                });
            }
        }
        return AccentColor();
    }

    private Color AccentColor() =>
        this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var value) && value is Color c
            ? c
            : Color.FromRgb(0xE7, 0x48, 0x56);

    /// <summary>The glow: one ellipse filled with a radial gradient (soft by construction, no
    /// blur effect). Moving it during a glide re-records just that ellipse; fading it is its
    /// Opacity, which does not re-record anything.</summary>
    private sealed class GlowLayer : Control
    {
        public Rect Ellipse { get; private set; }
        public Color Color { get; private set; }
        public int Renders { get; private set; }

        public void Set(Rect ellipse, Color color)
        {
            if (Color == color && Math.Abs(Ellipse.X - ellipse.X) < 0.25 && Math.Abs(Ellipse.Y - ellipse.Y) < 0.25
                && Math.Abs(Ellipse.Width - ellipse.Width) < 0.25 && Math.Abs(Ellipse.Height - ellipse.Height) < 0.25)
                return;
            Ellipse = ellipse;
            Color = color;
            InvalidateVisual();
        }

        private IBrush? _brush;
        private Color _brushColor;

        public override void Render(DrawingContext context)
        {
            Renders++;
            if (Ellipse.Width <= 0 || Ellipse.Height <= 0) return;
            // Relative stops: the same brush fills the ellipse wherever it moves; a new one
            // only for a new colour (a cross-fade between two covers).
            if (_brush == null || _brushColor != Color)
            {
                _brushColor = Color;
                _brush = MakeBrush(Color);
            }
            context.DrawEllipse(_brush, null, Ellipse.Center, Ellipse.Width / 2, Ellipse.Height / 2);
        }

        private static IBrush MakeBrush(Color c) => new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x8C, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x66, c.R, c.G, c.B), 0.5),
                new GradientStop(Color.FromArgb(0x26, c.R, c.G, c.B), 0.76),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
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
            _title.Text = _subtitle.Text = _details.Text = _counter.Text = string.Empty;
            _explicit.IsVisible = _qualityDot.IsVisible = _quality.IsVisible = false;
            return;
        }
        var album = _items[index];
        _title.Text = album.Name;
        _explicit.IsVisible = album.IsExplicit;
        _subtitle.Text = album.ArtistDisplay;
        CaptionCreditLink.SetCredit(_subtitle, album.ArtistDisplay);
        _details.Text = (string)MetadataLine.Convert(
            new object?[] { album.GenreDisplay, album.ReleaseDateFormatted, album.TrackCountText, album.HeaderDurationFormatted },
            typeof(string), null, System.Globalization.CultureInfo.CurrentCulture);
        var badge = album.AudioQualityBadge;
        _qualityText.Text = badge;
        _qualityTipTitle.Text = badge;
        _qualityTipInfo.Text = album.AudioQualityDetailedInfo;
        _qualityTipDescription.Text = album.AudioQualityDescription;
        _quality.IsVisible = !string.IsNullOrEmpty(badge);
        _qualityDot.IsVisible = _quality.IsVisible && !string.IsNullOrEmpty(_details.Text);
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
        var cardW = CentredWidth();
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
        AlbumCarouselStyleOverrides.Changed += OnStylePrefsChanged;
        OnStylePrefsChanged(null, EventArgs.Empty);
        if (IsVisible) FocusSoon();
        _bleed.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AlbumCarouselStyleOverrides.Changed -= OnStylePrefsChanged;
        _bleed.Detach();
        ClearValue(ClipProperty);
        _reachLeft = _reachRight = 0;
    }

    /// <summary>An album's own style or the glow switch changed (or the carousel came on
    /// screen): re-read them. Without the app's settings (tests) the set values stay.</summary>
    private void OnStylePrefsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnStylePrefsChanged(sender, e));
            return;
        }
        if (AlbumCarouselStyleOverrides.Current is not { } prefs) return;
        _glowEnabled = prefs.Glow;
        StyleOverrides = prefs.Overrides;
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

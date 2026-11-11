using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// The Albums page's Masonry layout (GitHub #213). Covers keep the grid's square size; each
/// card stacks under the shortest column by its real height, so a one-line caption no longer
/// leaves a row-wide gap.
///
/// Virtualized by hand, the way <see cref="AlbumCarousel"/> is: the panel sits in a
/// ScrollViewer and claims the full stack height, but only the cards within about one
/// viewport of the visible band are realized, from a recycled pool built with
/// <see cref="ItemTemplate"/> (the grid's own tile template). Cards that leave the band are
/// hidden and reused, so a library of thousands keeps a few dozen tiles alive.
///
/// Heights: a card's height depends only on how many lines its title wraps to (1 or 2; the
/// subtitle is one line). The line count comes from a TextLayout in the tile's own title font
/// and width, and the two heights are learned from realized tiles. A realized card whose
/// measured height still differs (an explicit badge or heart changed the wrap) replaces its
/// estimate and the stack is re-placed, which is O(n) over plain arrays.
/// </summary>
public sealed class MasonryAlbumPanel : Panel
{
    public static readonly StyledProperty<IReadOnlyList<Album>?> AlbumsProperty =
        AvaloniaProperty.Register<MasonryAlbumPanel, IReadOnlyList<Album>?>(nameof(Albums));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<MasonryAlbumPanel, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<MasonryAlbumPanel, int>(nameof(Columns), 5);

    /// <summary>The covers' edge; a change re-estimates every card.</summary>
    public static readonly StyledProperty<double> ArtworkSizeProperty =
        AvaloniaProperty.Register<MasonryAlbumPanel, double>(nameof(ArtworkSize), 180);

    /// <summary>Extra space between cards stacked in one column (on top of the tiles' own margins).</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<MasonryAlbumPanel, double>(nameof(Spacing));

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public IReadOnlyList<Album>? Albums { get => GetValue(AlbumsProperty); set => SetValue(AlbumsProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }
    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ArtworkSize { get => GetValue(ArtworkSizeProperty); set => SetValue(ArtworkSizeProperty, value); }

    /// <summary>A pooled tile was (re)bound to an album: the host re-applies per-album state
    /// (the Ctrl-selection plate) that a recycled tile would otherwise carry over.</summary>
    public event EventHandler<Control>? ElementPrepared;

    /// <summary>Realize this many px above and below the visible band.</summary>
    private const double Buffer = 600;

    private IReadOnlyList<Album> _items = Array.Empty<Album>();
    private double[] _heights = Array.Empty<double>();
    private bool[] _measured = Array.Empty<bool>();
    private int[] _column = Array.Empty<int>();
    private double[] _top = Array.Empty<double>();
    private double _total;
    private double _width;
    private int _columns = 1;
    private bool _dirty = true;

    private readonly Dictionary<int, Control> _realized = new();
    private readonly Stack<Control> _pool = new();
    private readonly List<int> _visible = new();
    private Rect _viewport;
    private double _bandTop, _bandBottom = -1;

    // Learned card heights for a one- and a two-line title, and the title's font and width.
    private double _oneLine = double.NaN, _twoLine = double.NaN;
    private Typeface? _titleFace;
    private double _titleSize = 13, _titleWidth = double.NaN;

    public MasonryAlbumPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            // Re-measure only when the visible band leaves the realized one.
            if (_viewport.Top < _bandTop || _viewport.Bottom > _bandBottom)
                InvalidateMeasure();
        };
    }

    /// <summary>The realized (visible) tiles, for the host's selection sweeps.</summary>
    public IEnumerable<Control> RealizedElements => _realized.Values;

    /// <summary>
    /// The albums whose card meets <paramref name="rect"/> (panel coordinates), realized or
    /// not: the rubber band selects by the placed stack, so cards it sweeps past while
    /// scrolling stay selected (GitHub #226). A card is its slot less the tile's margin.
    /// </summary>
    public List<Album> AlbumsIn(Rect rect)
    {
        var hits = new List<Album>();
        if (_dirty || _width <= 0) return hits;
        var margin = ItemTemplateMargin();
        var cell = CellWidth;
        for (var i = 0; i < _items.Count && i < _top.Length; i++)
        {
            var card = new Rect(_column[i] * cell, _top[i], cell, Math.Max(0, _heights[i] - Spacing)).Deflate(margin);
            if (card.Intersects(rect)) hits.Add(_items[i]);
        }
        return hits;
    }

    private Thickness ItemTemplateMargin()
    {
        foreach (var c in _realized.Values) return c.Margin;
        return default;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AlbumsProperty)
        {
            _items = Albums ?? (IReadOnlyList<Album>)Array.Empty<Album>();
            RecycleAll();
            _dirty = true;
            InvalidateMeasure();
        }
        else if (change.Property == ColumnsProperty || change.Property == ArtworkSizeProperty || change.Property == SpacingProperty)
        {
            // New widths re-wrap titles: forget the learned heights.
            _oneLine = _twoLine = _titleWidth = double.NaN;
            _dirty = true;
            InvalidateMeasure();
        }
        else if (change.Property == ItemTemplateProperty)
        {
            RecycleAll();
            Children.Clear();
            _pool.Clear();
            InvalidateMeasure();
        }
    }

    private void RecycleAll()
    {
        foreach (var c in _realized.Values) Park(c);
        _realized.Clear();
        _bandBottom = -1;
    }

    private void Park(Control c)
    {
        c.IsVisible = false;
        _pool.Push(c);
    }

    private double CellWidth => _width / _columns;

    /// <summary>Card height before it is ever realized.</summary>
    private double Estimate(Album album)
    {
        var lines = TitleLines(album.Name);
        if (lines <= 1 && !double.IsNaN(_oneLine)) return _oneLine;
        if (lines >= 2 && !double.IsNaN(_twoLine)) return _twoLine;
        if (!double.IsNaN(_oneLine)) return _oneLine + (lines - 1) * 17;
        if (!double.IsNaN(_twoLine)) return _twoLine - (2 - lines) * 17;
        // Nothing realized yet: cover + tile chrome + title lines + subtitle.
        return ArtworkSize + 18 + 5 + lines * 17 + 1 + 16;
    }

    private int TitleLines(string? title)
    {
        if (_titleFace is not { } face || double.IsNaN(_titleWidth) || string.IsNullOrEmpty(title)) return 1;
        using var layout = new TextLayout(title, face, _titleSize, null, TextAlignment.Left,
            TextWrapping.Wrap, TextTrimming.CharacterEllipsis, maxWidth: _titleWidth, maxLines: 2);
        return Math.Clamp(layout.TextLines.Count, 1, 2);
    }

    private void Replace()
    {
        var n = _items.Count;
        if (_heights.Length != n)
        {
            _heights = new double[n];
            _measured = new bool[n];
            _column = new int[n];
            _top = new double[n];
        }
        for (var i = 0; i < n; i++)
        {
            _heights[i] = Estimate(_items[i]) + Spacing;
            _measured[i] = false;
        }
        Place();
        _dirty = false;
    }

    private void Place() => _total = MasonryLayout.Place(_heights, _columns, _column, _top);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width;
        var columns = Math.Max(1, Columns);
        if (Math.Abs(width - _width) > 0.5 || columns != _columns)
        {
            _width = width;
            _columns = columns;
            _dirty = true;
        }
        if (_dirty) Replace();
        if (_width <= 0 || _items.Count == 0)
        {
            RecycleAll();
            return new Size(0, 0);
        }

        // The band to realize: the visible part plus a buffer. Before the first viewport
        // report, assume the top of the stack.
        var vp = _viewport.Height > 0 ? _viewport : new Rect(0, 0, _width, 1000);
        var from = Math.Max(0, vp.Top - Buffer);
        var to = vp.Bottom + Buffer;

        for (var pass = 0; pass < 3; pass++)
        {
            MasonryLayout.Visible(_heights, _column, _top, _columns, from, to, _visible);
            var keep = new HashSet<int>(_visible);
            var gone = new List<int>();
            foreach (var (i, c) in _realized) if (!keep.Contains(i)) gone.Add(i);
            foreach (var i in gone) { Park(_realized[i]); _realized.Remove(i); }

            var changed = false;
            foreach (var i in _visible)
            {
                if (!_realized.TryGetValue(i, out var c))
                {
                    c = Rent();
                    if (c == null) break;
                    c.DataContext = _items[i];
                    c.IsVisible = true;
                    _realized[i] = c;
                    ElementPrepared?.Invoke(this, c);
                }
                c.Measure(new Size(CellWidth, double.PositiveInfinity));
                var tileHeight = c.DesiredSize.Height;
                Learn(c, _items[i], tileHeight);
                var h = tileHeight + Spacing;
                _measured[i] = true;
                if (Math.Abs(h - _heights[i]) > 0.5)
                {
                    _heights[i] = h;
                    changed = true;
                }
            }
            if (!changed) break;
            // Real heights moved cards below them: re-place and re-check the band. Unmeasured
            // cards take the freshly learned heights too.
            for (var i = 0; i < _items.Count; i++)
                if (!_measured[i]) _heights[i] = Estimate(_items[i]) + Spacing;
            Place();
        }

        _bandTop = from;
        _bandBottom = to;
        return new Size(_width, _total);
    }

    private void Learn(Control tile, Album album, double height)
    {
        if (_titleFace == null || double.IsNaN(_titleWidth))
        {
            foreach (var v in tile.GetVisualDescendants())
            {
                if (v is TextBlock tb && TitleWrap.GetWatch(tb))
                {
                    _titleFace = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight);
                    _titleSize = tb.FontSize;
                    if (tb.DesiredSize.Width > 0) _titleWidth = Math.Max(1, tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right);
                    break;
                }
            }
            if (double.IsNaN(_titleWidth)) _titleWidth = Math.Max(1, CellWidth - 18 - 6);
        }
        // Favorite hearts and explicit badges narrow the title: learn only from plain cards.
        if (album.HasFavoriteTrack || album.IsExplicit) return;
        if (TitleLines(album.Name) <= 1) { if (double.IsNaN(_oneLine)) _oneLine = height; }
        else if (double.IsNaN(_twoLine)) _twoLine = height;
    }

    private Control? Rent()
    {
        if (_pool.Count > 0) return _pool.Pop();
        if (ItemTemplate?.Build(null) is not Control c) return null;
        Children.Add(c);
        return c;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cell = CellWidth;
        foreach (var (i, c) in _realized)
            c.Arrange(new Rect(_column[i] * cell, _top[i], cell, Math.Max(0, _heights[i] - Spacing)));
        return new Size(finalSize.Width, _total);
    }
}

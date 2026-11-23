using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
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
/// estimate and the stack is re-placed, which is O(n) over plain arrays: each title's line
/// count is laid out once and kept (GitHub #241).
///
/// Titles are laid out only as far down as the realized band reaches (a card's place depends
/// only on the cards before it); the rest of the stack takes an average estimate and is laid
/// out in short idle-time slices. Laying out every title up front, twice, cost ~1.2 s on the
/// UI thread when Masonry was switched on for 5000 albums (GitHub #259).
///
/// Also hosts the Genre page's albums (under its Songs section), the artist page's release
/// tabs and Home's album sections (GitHub #259); an observable <see cref="Albums"/> list is
/// followed as it fills.
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

    /// <summary>Realize every card, not only those near the viewport: for short lists whose host
    /// tracks the selection by tile (Home's album sections), where a recycled tile would carry
    /// another album's selection.</summary>
    public static readonly StyledProperty<bool> RealizeAllProperty =
        AvaloniaProperty.Register<MasonryAlbumPanel, bool>(nameof(RealizeAll));

    public bool RealizeAll { get => GetValue(RealizeAllProperty); set => SetValue(RealizeAllProperty, value); }
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
    private byte[] _lines = Array.Empty<byte>(); // title line count per card; 0 = not laid out yet
    private int _laidOut;       // every title before this index has its line count
    private int _twoLineTitles; // of those, how many wrap to two lines
    private bool _fillPosted, _estimatesStale;
    private INotifyCollectionChanged? _observed;
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
    private bool _titleWidthExact;

    public MasonryAlbumPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            // Re-measure only when the visible band leaves the realized one.
            if (!RealizeAll && (_viewport.Top < _bandTop || _viewport.Bottom > _bandBottom))
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
            if (VisualRoot != null) Observe(_items);
            ResetItems();
        }
        else if (change.Property == RealizeAllProperty)
        {
            _bandBottom = -1;
            InvalidateMeasure();
        }
        else if (change.Property == ColumnsProperty || change.Property == ArtworkSizeProperty || change.Property == SpacingProperty)
        {
            // New widths re-wrap titles: forget the learned heights.
            _oneLine = _twoLine = _titleWidth = double.NaN;
            _titleWidthExact = false;
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The list may have changed while the panel was out of the tree.
        Observe(_items);
        ResetItems();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Observe(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Follows an observable list (the artist page's tabs stream in, Home's sections
    /// refill); subscribed only while in the tree, so a view model that outlives the view does
    /// not hold the panel.</summary>
    private void Observe(IReadOnlyList<Album>? items)
    {
        if (_observed != null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = items as INotifyCollectionChanged;
        if (_observed != null) _observed.CollectionChanged += OnItemsChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Albums appended at the end leave every placed card and laid-out title as it was.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null
            && e.NewStartingIndex + e.NewItems.Count == _items.Count)
        {
            _dirty = true;
            InvalidateMeasure();
            return;
        }
        ResetItems();
    }

    private void ResetItems()
    {
        RecycleAll();
        ClearLines();
        _dirty = true;
        InvalidateMeasure();
    }

    private void ClearLines()
    {
        Array.Clear(_lines);
        _laidOut = 0;
        _twoLineTitles = 0;
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

    /// <summary>Card height before it is ever realized: by its title's line count once that is
    /// laid out, else by the share of two-line titles laid out so far.</summary>
    private double Estimate(int index)
    {
        double lines = _lines[index];
        if (lines == 0)
            lines = _laidOut > 0 ? 1 + (double)_twoLineTitles / _laidOut : 1.25;
        if (!double.IsNaN(_oneLine) && !double.IsNaN(_twoLine)) return _oneLine + (lines - 1) * (_twoLine - _oneLine);
        if (!double.IsNaN(_oneLine)) return _oneLine + (lines - 1) * 17;
        if (!double.IsNaN(_twoLine)) return _twoLine - (2 - lines) * 17;
        // Nothing realized yet: cover + tile chrome + title lines + subtitle.
        return ArtworkSize + 18 + 5 + lines * 17 + 1 + 16;
    }

    /// <summary>Titles laid out with a TextLayout so far (tests pin the cost by it).</summary>
    internal int TitleLayoutCount { get; private set; }

    private bool CanLayOutTitles => _titleFace != null && !double.IsNaN(_titleWidth);

    /// <summary>Lays out the titles of cards [<see cref="_laidOut"/>, <paramref name="end"/>).</summary>
    private void LayOutTitles(int end)
    {
        if (!CanLayOutTitles) return;
        end = Math.Min(end, _items.Count);
        for (; _laidOut < end; _laidOut++)
            if (TitleLines(_laidOut) >= 2) _twoLineTitles++;
    }

    /// <summary>
    /// Every card that starts above <paramref name="bottom"/> gets its real title line count,
    /// re-placing the stack until that holds. Cards are placed in index order, so the ones
    /// further down never move these.
    /// </summary>
    private void LayOutTitlesThrough(double bottom)
    {
        if (!CanLayOutTitles) return;
        for (var guard = 0; guard < 16 && _laidOut < _items.Count; guard++)
        {
            var last = LastCardAbove(bottom);
            if (last < _laidOut) return;
            LayOutTitles(last + 1 + _columns);
            Reestimate();
        }
    }

    /// <summary>The highest index whose card starts above <paramref name="bottom"/>, scanning
    /// from the first card whose title is not laid out yet (-1 when none).</summary>
    private int LastCardAbove(double bottom)
    {
        var last = -1;
        var passed = 0;
        Span<bool> done = _columns <= 64 ? stackalloc bool[_columns] : new bool[_columns];
        done.Clear();
        for (var i = _laidOut; i < _items.Count && passed < _columns; i++)
        {
            if (_top[i] < bottom) { last = i; continue; }
            if (!done[_column[i]]) { done[_column[i]] = true; passed++; }
        }
        return last;
    }

    /// <summary>Unmeasured cards take the current estimates; the stack is re-placed.</summary>
    private void Reestimate()
    {
        for (var i = 0; i < _items.Count; i++)
            if (!_measured[i]) _heights[i] = Estimate(i) + Spacing;
        Place();
    }

    /// <summary>The rest of the titles, a few milliseconds at a time with frames between, so
    /// the scroll extent settles and a jump down the list finds them laid out. A timer rather
    /// than a chained idle post, which ran back to back with only the layout in between.</summary>
    private void PostTitleFill()
    {
        if (_fillPosted || RealizeAll || _laidOut >= _items.Count || !CanLayOutTitles) return;
        _fillPosted = true;
        DispatcherTimer.RunOnce(() =>
        {
            _fillPosted = false;
            if (VisualRoot == null || _dirty || !CanLayOutTitles || _laidOut >= _items.Count) return;
            var sw = Stopwatch.StartNew();
            while (_laidOut < _items.Count && sw.ElapsedMilliseconds < 8)
                LayOutTitles(_laidOut + 8);
            _estimatesStale = true;
            InvalidateMeasure();
        }, TimeSpan.FromMilliseconds(16), DispatcherPriority.Background);
    }

    /// <summary>Title line count of card <paramref name="index"/>, laid out once per title
    /// font and width (GitHub #241: re-laying out every title whenever a realized card
    /// corrected its estimate cost ~2000 TextLayouts, hundreds of ms, per scroll step).</summary>
    private int TitleLines(int index)
    {
        if (_lines[index] != 0) return _lines[index];
        var title = _items[index].Name;
        if (_titleFace is not { } face || double.IsNaN(_titleWidth)) return 1;
        if (string.IsNullOrEmpty(title)) return _lines[index] = 1;
        TitleLayoutCount++;
        using var layout = new TextLayout(title, face, _titleSize, null, TextAlignment.Left,
            TextWrapping.Wrap, TextTrimming.CharacterEllipsis, maxWidth: _titleWidth, maxLines: 2);
        var lines = (byte)Math.Clamp(layout.TextLines.Count, 1, 2);
        _lines[index] = lines;
        return lines;
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
            // Appended albums keep the titles laid out before them (a new list or a new title
            // width has cleared them already).
            Array.Resize(ref _lines, n);
            _laidOut = Math.Min(_laidOut, n);
        }
        // Line counts stay: they change only with the albums (ResetItems) or the title's width
        // (Learn). Clearing them here laid every title out again on each width change (#259).
        for (var i = 0; i < n; i++)
        {
            _heights[i] = Estimate(i) + Spacing;
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
            _titleWidthExact = false; // a wider cell re-wraps titles: re-learn from the next plain card
        }
        if (_dirty) Replace();
        else if (_estimatesStale) Reestimate();
        _estimatesStale = false;
        if (_width <= 0 || _items.Count == 0)
        {
            RecycleAll();
            return new Size(0, 0);
        }

        // The band to realize: the visible part plus a buffer. Before the first viewport
        // report, assume the top of the stack.
        var vp = _viewport.Height > 0 ? _viewport : new Rect(0, 0, _width, 1000);
        var from = RealizeAll ? 0 : Math.Max(0, vp.Top - Buffer);
        var to = RealizeAll ? double.PositiveInfinity : vp.Bottom + Buffer;

        for (var pass = 0; pass < 3; pass++)
        {
            LayOutTitlesThrough(to);
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
                    c = Rent(_items[i]);
                    if (c == null) break;
                    c.DataContext = _items[i];
                    c.IsVisible = true;
                    _realized[i] = c;
                    ElementPrepared?.Invoke(this, c);
                    InvalidateRebound(c);
                }
                c.Measure(new Size(CellWidth, double.PositiveInfinity));
                var tileHeight = c.DesiredSize.Height;
                Learn(c, i, tileHeight);
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
            Reestimate();
        }

        _bandTop = from;
        _bandBottom = to;
        PostTitleFill();
        return new Size(_width, _total);
    }

    /// <summary>
    /// A pooled tile re-bound to another album: its title's new text invalidates only the
    /// TextBlock, while the tile and the panels between stay measure-valid, so measuring the
    /// tile returned the previous album's height and the real one landed a layout pass later,
    /// re-placing the stack once more (GitHub #241). Invalidate the chain up to the tile.
    /// </summary>
    private static void InvalidateRebound(Control tile)
    {
        foreach (var v in tile.GetVisualDescendants())
        {
            if (v is not Layoutable { IsMeasureValid: false }) continue;
            for (var p = v.GetVisualParent(); p is Layoutable l; p = p.GetVisualParent())
            {
                l.InvalidateMeasure();
                if (ReferenceEquals(l, tile)) break;
            }
        }
    }

    private void Learn(Control tile, int index, double height)
    {
        var album = _items[index];
        if (_titleFace == null || double.IsNaN(_titleWidth) || (!_titleWidthExact && !album.HasFavoriteTrack))
        {
            foreach (var v in tile.GetVisualDescendants())
            {
                if (v is TextBlock tb && TitleWrap.GetWatch(tb))
                {
                    _titleFace = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight);
                    _titleSize = tb.FontSize;
                    // The width the title wraps at is the one it was measured in (a favorite's
                    // heart narrows it), not its desired width: that is the first title's own
                    // length, which made longer titles estimate two lines and every realized
                    // card correct the stack (GitHub #241).
                    if (!album.HasFavoriteTrack
                        && LayoutInformation.GetPreviousMeasureConstraint(tb) is { Width: > 0 } box
                        && double.IsFinite(box.Width))
                    {
                        var width = Math.Max(1, box.Width - tb.Margin.Left - tb.Margin.Right - tb.Padding.Left - tb.Padding.Right);
                        if (Math.Abs(width - _titleWidth) > 0.01 || double.IsNaN(_titleWidth)) ClearLines();
                        _titleWidth = width;
                        _titleWidthExact = true;
                    }
                    break;
                }
            }
            if (double.IsNaN(_titleWidth)) _titleWidth = Math.Max(1, CellWidth - 18 - 6);
        }
        // Favorite hearts and explicit badges narrow the title: learn only from plain cards.
        if (album.HasFavoriteTrack || album.IsExplicit) return;
        if (TitleLines(index) <= 1) { if (double.IsNaN(_oneLine)) _oneLine = height; }
        else if (double.IsNaN(_twoLine)) _twoLine = height;
    }

    /// <summary>A pooled tile, or a new one that joins the panel already holding its album: added
    /// first, it took the page's view model as its data for a moment and its Command bindings
    /// (open the album, the cover's Play) stayed null.</summary>
    private Control? Rent(Album album)
    {
        if (_pool.Count > 0) return _pool.Pop();
        if (ItemTemplate?.Build(album) is not Control c) return null;
        c.DataContext = album;
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

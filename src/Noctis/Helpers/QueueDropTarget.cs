using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// GitHub #189: a queue list that takes files and folders dragged in from Explorer (and, for
/// the queue panel, rows dragged from the library) where the pointer is, instead of at the
/// end. While a drag is over it an insertion line sits between the rows the drop would land
/// between, following the pointer, and holding the pointer near the list's top or bottom
/// edge scrolls it (the band selection's edge scroll, <see cref="QueueBandAutoScroll"/>).
/// MainWindow routes the window's drag events here: <see cref="At"/> picks the list under
/// the pointer, <see cref="Track"/> places the line and returns the Up Next index.
/// </summary>
public sealed class QueueDropTarget
{
    /// <summary>Edge zone that scrolls the list, as for the queue band selection.</summary>
    private const double EdgePx = 28.0;

    private static readonly List<QueueDropTarget> Targets = new();

    private readonly ListBox _list;
    private readonly Control _line;
    private readonly Func<int, int> _rowToUpNext;
    private readonly Func<int, int> _upNextToRow;
    private readonly Func<bool>? _isActive;
    private readonly TranslateTransform _lineOffset = new();
    private double _pointerY;
    private bool _tracking;
    private bool _frameQueued;
    private long _lastFrameTicks;

    /// <param name="list">The queue list.</param>
    /// <param name="line">The insertion line: top-aligned in a panel over the list, not hit-testable.</param>
    /// <param name="rowToUpNext">Row the drop would land above → Up Next index.</param>
    /// <param name="upNextToRow">Up Next index → row the line is drawn above.</param>
    /// <param name="isActive">Extra condition for taking drops (e.g. the queue panel is open).</param>
    public QueueDropTarget(ListBox list, Control line, Func<int, int> rowToUpNext, Func<int, int> upNextToRow,
        Func<bool>? isActive = null)
    {
        _list = list;
        _line = line;
        _rowToUpNext = rowToUpNext;
        _upNextToRow = upNextToRow;
        _isActive = isActive;
        _line.RenderTransform = _lineOffset;
    }

    /// <summary>The higher wins where two lists overlap (the queue panel over the Queue page).</summary>
    public int Priority { get; init; }

    /// <summary>Rows dragged from the library drop here too (the queue panel, GitHub #71). The
    /// Queue page's own rows are drag sources, so dropping them back would duplicate them.</summary>
    public bool AcceptsInternalRows { get; init; }

    /// <summary>The Up Next index the last <see cref="Track"/> picked.</summary>
    public int InsertIndex { get; private set; }

    public static void Register(QueueDropTarget target)
    {
        if (!Targets.Contains(target)) Targets.Add(target);
    }

    public static void Unregister(QueueDropTarget target)
    {
        target.Hide();
        Targets.Remove(target);
    }

    /// <summary>The list under the pointer that takes drops, or null. <paramref name="positionIn"/>
    /// maps the pointer into a visual's coordinates (DragEventArgs.GetPosition).</summary>
    public static QueueDropTarget? At(Func<Visual, Point> positionIn)
    {
        QueueDropTarget? best = null;
        foreach (var target in Targets)
        {
            if (target.Contains(positionIn) && (best == null || target.Priority > best.Priority))
                best = target;
        }
        return best;
    }

    /// <summary>Hides every line but <paramref name="keep"/>'s.</summary>
    public static void HideAll(QueueDropTarget? keep = null)
    {
        foreach (var target in Targets)
        {
            if (!ReferenceEquals(target, keep)) target.Hide();
        }
    }

    public bool Contains(Func<Visual, Point> positionIn)
    {
        if (!_list.IsEffectivelyVisible || (_isActive != null && !_isActive())) return false;
        if (TopLevel.GetTopLevel(_list) == null) return false;
        return new Rect(_list.Bounds.Size).Contains(positionIn(_list));
    }

    /// <summary>Places the line for the pointer and returns the Up Next index a drop there lands at.</summary>
    public int Track(Func<Visual, Point> positionIn)
    {
        _pointerY = positionIn(_list).Y;
        _tracking = true;
        Update();
        RequestFrame();
        return InsertIndex;
    }

    public void Hide()
    {
        _tracking = false;
        _line.IsVisible = false;
    }

    private void Update()
    {
        var rows = RealizedRows();
        var height = _list.Bounds.Height;
        var row = InsertRowAt(rows, _pointerY, height, _list.ItemCount);
        InsertIndex = _rowToUpNext(row);
        var lineY = LineY(rows, _upNextToRow(InsertIndex), height, _list.ItemCount);
        if (lineY is { } y && _line.GetVisualParent() is Visual parent
            && _list.TranslatePoint(new Point(0, y), parent) is { } p)
        {
            var thickness = double.IsNaN(_line.Height) ? _line.Bounds.Height : _line.Height;
            _lineOffset.Y = p.Y - _line.Margin.Top - thickness / 2;
            _line.IsVisible = true;
        }
        else
        {
            _line.IsVisible = false;
        }
    }

    /// <summary>Realized rows in list coordinates, margins included (adjacent rows touch).</summary>
    private List<(int Index, double Top, double Height)> RealizedRows()
    {
        var rows = new List<(int, double, double)>();
        foreach (var container in _list.GetRealizedContainers())
        {
            var i = _list.IndexFromContainer(container);
            if (i < 0 || !container.IsVisible) continue;
            if (container.TranslatePoint(new Point(0, 0), _list) is not { } p) continue;
            rows.Add((i, p.Y - container.Margin.Top,
                container.Bounds.Height + container.Margin.Top + container.Margin.Bottom));
        }
        return rows;
    }

    /// <summary>
    /// The row a drop at <paramref name="pointerY"/> lands above: the first on-screen row whose
    /// middle is below the pointer, else the one after the last on-screen row (the end when
    /// nothing is realized). Rows are (index, top, height) in list coordinates; a container kept
    /// realized far off-screen (the focused one) is ignored.
    /// </summary>
    public static int InsertRowAt(IReadOnlyList<(int Index, double Top, double Height)> rows, double pointerY,
        double viewportHeight, int rowCount)
    {
        var last = -1;
        foreach (var row in rows.Where(r => OnScreen(r, viewportHeight)).OrderBy(r => r.Top))
        {
            if (pointerY < row.Top + row.Height / 2) return row.Index;
            last = row.Index;
        }
        return last >= 0 ? last + 1 : rowCount;
    }

    /// <summary>Where the line above <paramref name="row"/> goes, in list coordinates: that row's
    /// top, or the bottom of the row before it (the end); the list's top when it is empty; null
    /// when neither row is on screen.</summary>
    public static double? LineY(IReadOnlyList<(int Index, double Top, double Height)> rows, int row,
        double viewportHeight, int rowCount)
    {
        if (rowCount == 0) return 0;
        foreach (var r in rows)
        {
            if (r.Index == row && OnScreen(r, viewportHeight)) return r.Top;
        }
        foreach (var r in rows)
        {
            if (r.Index == row - 1 && OnScreen(r, viewportHeight)) return r.Top + r.Height;
        }
        return null;
    }

    private static bool OnScreen((int Index, double Top, double Height) r, double viewportHeight)
        => r.Height > 0 && r.Top + r.Height >= -r.Height && r.Top <= viewportHeight + r.Height;

    // ── Edge scroll ──

    private void RequestFrame()
    {
        if (_frameQueued || !_tracking) return;
        if (TopLevel.GetTopLevel(_list) is not { } top) return;
        _frameQueued = true;
        _lastFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameQueued = false;
        if (!_tracking) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        // Real elapsed time, clamped so a stalled UI thread can't produce one giant jump.
        var dt = Math.Min((now - _lastFrameTicks) / (double)System.Diagnostics.Stopwatch.Frequency, 0.1);
        var delta = QueueBandAutoScroll.Step(_pointerY, _list.Bounds.Height, EdgePx, dt);
        // Out of the edge zones: stop; the next DragOver starts the loop again.
        if (delta == 0 || _list.FindDescendantOfType<ScrollViewer>() is not { } scroller) return;
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var next = Math.Clamp(scroller.Offset.Y + delta, 0, max);
        if (Math.Abs(next - scroller.Offset.Y) > 0.01)
        {
            scroller.Offset = scroller.Offset.WithY(next);
            // Realize/arrange the rows at the new offset before measuring off them.
            _list.UpdateLayout();
            Update();
        }
        RequestFrame();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Localization;

namespace Noctis.Controls;

/// <summary>
/// A list's column header (GitHub #151): the header cells laid out by the shared
/// <see cref="ColumnLayout"/> like every row, plus
/// <list type="bullet">
/// <item>a divider at each cell's right edge: drag to resize, double-click for the automatic width;</item>
/// <item>drag a header cell sideways to move its column (an accent bar shows where it lands);</item>
/// <item>a small arrow by the label of the column the list is sorted by, pointing up for ascending;</item>
/// <item>right-click for a menu of the columns, with ticks, and Reset Columns.</item>
/// </list>
/// Clicking a cell still runs its own command (sorting), as before.
/// </summary>
public sealed class ColumnHeaderPanel : ColumnsPanel
{
    /// <summary>Sort key of the column the list is sorted by (<see cref="ColumnSpec.SortKey"/>).</summary>
    public static readonly StyledProperty<string?> SortKeyProperty =
        AvaloniaProperty.Register<ColumnHeaderPanel, string?>(nameof(SortKey));

    public static readonly StyledProperty<bool> SortAscendingProperty =
        AvaloniaProperty.Register<ColumnHeaderPanel, bool>(nameof(SortAscending), true);

    public string? SortKey { get => GetValue(SortKeyProperty); set => SetValue(SortKeyProperty, value); }
    public bool SortAscending { get => GetValue(SortAscendingProperty); set => SetValue(SortAscendingProperty, value); }

    /// <summary>Half the divider's hit width on each side of a column edge.</summary>
    private const double GripHalf = 4;
    /// <summary>Sideways travel before a press on a cell becomes a column move.</summary>
    private const double DragThreshold = 6;

    private readonly Dictionary<string, Border> _grips = new(StringComparer.Ordinal);
    private readonly Border _dropMarker;
    private readonly Path _sortArrow;

    private string? _resizeKey;
    private double _resizeStartX, _resizeStartWidth;

    private Control? _pressCell;
    private Point _pressPoint;
    private bool _moving;
    private int _dropIndex = -1;

    static ColumnHeaderPanel()
    {
        AffectsArrange<ColumnHeaderPanel>(SortKeyProperty, SortAscendingProperty);
    }

    public ColumnHeaderPanel()
    {
        Background = Brushes.Transparent;
        _dropMarker = new Border { Width = 2, IsVisible = false, IsHitTestVisible = false, CornerRadius = new CornerRadius(1) };
        _dropMarker.Bind(Border.BackgroundProperty, _dropMarker.GetResourceObservable("AccentColorBrush"));
        VisualChildren.Add(_dropMarker);
        _sortArrow = new Path { Width = 7, Height = 4, Stretch = Stretch.None, IsHitTestVisible = false, Opacity = 0.7, IsVisible = false };
        // Same colour as the header labels (inherited text foreground); the Fluent brush key it used
        // resolved to nothing here, so the arrow drew with no fill.
        _sortArrow.Bind(Shape.FillProperty, this.GetObservable(TextElement.ForegroundProperty));
        VisualChildren.Add(_sortArrow);

        AddHandler(PointerPressedEvent, OnCellPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnCellPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnCellPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndMove(commit: false));
        ContextRequested += OnContextRequested;
    }

    private static readonly IBrush DividerBrush = new SolidColorBrush(Color.FromArgb(0x70, 0x90, 0x90, 0x90));

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        foreach (var grip in _grips.Values) grip.Child!.Opacity = grip.IsPointerOver ? 1 : 0.5;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_resizeKey != null) return;
        foreach (var grip in _grips.Values) grip.Child!.Opacity = 0;
    }

    /// <summary>The divider for <paramref name="key"/>, for tests and accessibility tools.</summary>
    internal Border? GripFor(string key) => _grips.GetValueOrDefault(key);

    protected override void OnLayoutChanged(object? sender, EventArgs e)
    {
        SyncGrips();
        base.OnLayoutChanged(sender, e);
    }

    /// <summary>One divider per shown, resizable column; kept above the cells in the visual list.</summary>
    private void SyncGrips()
    {
        var layout = Layout;
        var wanted = layout?.Visible.Where(s => !s.Pinned).Select(s => s.Key).ToHashSet() ?? new HashSet<string>();
        foreach (var key in _grips.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            VisualChildren.Remove(_grips[key]);
            _grips.Remove(key);
        }
        foreach (var key in wanted)
        {
            if (_grips.ContainsKey(key)) continue;
            var grip = new Border
            {
                Width = GripHalf * 2,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                Tag = key,
            };
            // A hairline in the middle so the divider is findable: shown while the pointer
            // is over the header, brighter over the divider itself.
            grip.Child = new Border
            {
                Width = 1,
                Margin = new Thickness(0, 3),
                Background = DividerBrush,
                IsHitTestVisible = false,
                Opacity = IsPointerOver ? 0.5 : 0,
            };
            var label = layout!.Find(key)?.Label;
            AutomationProperties.SetName(grip, string.Format(Loc.T("Columns.ResizeName"), label));
            grip.PointerEntered += (_, _) => grip.Child!.Opacity = 1;
            grip.PointerExited += (_, _) => grip.Child!.Opacity = IsPointerOver ? 0.5 : 0;
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
            grip.PointerCaptureLost += (_, _) => _resizeKey = null;
            _grips[key] = grip;
            // Before the drop marker, after every cell.
            VisualChildren.Insert(VisualChildren.IndexOf(_dropMarker), grip);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        foreach (var grip in _grips.Values) grip.Measure(new Size(GripHalf * 2, size.Height));
        _dropMarker.Measure(new Size(2, size.Height));
        _sortArrow.Measure(new Size(7, 4));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        base.ArrangeOverride(finalSize);
        var layout = Layout;
        if (layout != null)
        {
            var (widths, offsets) = layout.Compute(finalSize.Width);
            var cols = layout.Visible;
            for (var i = 0; i < cols.Count; i++)
                if (_grips.TryGetValue(cols[i].Key, out var grip))
                    grip.Arrange(new Rect(offsets[i] + widths[i] - GripHalf, 0, GripHalf * 2, finalSize.Height));
            if (_moving && _dropIndex >= 0)
                _dropMarker.Arrange(new Rect(DropX(layout, finalSize.Width, _dropIndex) - 1, 0, 2, finalSize.Height));
        }
        ArrangeSortArrow();
        return finalSize;
    }

    // ── Sort arrow ──

    private static readonly Geometry ArrowUp = Geometry.Parse("M0,4 L3.5,0 L7,4 Z");
    private static readonly Geometry ArrowDown = Geometry.Parse("M0,0 L3.5,4 L7,0 Z");

    /// <summary>
    /// Places the sort arrow just right of the sorted column's label (inside the cell), or
    /// hides it when the sort field has no shown column (Date Modified, a hidden column).
    /// </summary>
    internal Path SortArrow => _sortArrow;

    private void ArrangeSortArrow()
    {
        var layout = Layout;
        var spec = SortKey == null ? null : layout?.Visible.FirstOrDefault(s => s.SortKey == SortKey);
        var cell = spec == null ? null : CellFor(spec.Key);
        if (cell is not { IsVisible: true })
        {
            _sortArrow.IsVisible = false;
            return;
        }
        _sortArrow.Data = SortAscending ? ArrowUp : ArrowDown;
        _sortArrow.IsVisible = true;
        var label = cell.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        var x = label?.TranslatePoint(new Point(label.Bounds.Width, 0), this) is { } p
            ? p.X + 5
            : cell.Bounds.Right - 12;
        // Clamp to the column's slot, not the cell: header buttons are only as wide as their
        // label (left- or centre-aligned), so clamping to the cell put the arrow on the text.
        var cols = layout!.Visible;
        var i = -1;
        for (var k = 0; k < cols.Count; k++) if (cols[k].Key == spec!.Key) { i = k; break; }
        var (widths, offsets) = layout.Compute(Bounds.Width);
        var slotLeft = i >= 0 ? offsets[i] : cell.Bounds.Left;
        var slotRight = i >= 0 ? offsets[i] + widths[i] : cell.Bounds.Right;
        x = Math.Max(slotLeft, Math.Min(x, slotRight - 8));
        var y = cell.Bounds.Top + (cell.Bounds.Height - 4) / 2;
        _sortArrow.Arrange(new Rect(x, y, 7, 4));
    }

    private Control? CellFor(string key)
    {
        foreach (var child in Children)
            if (GetColumn(child) == key) return child;
        return null;
    }

    // ── Resize ──

    private void OnGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string key } grip || Layout is not { } layout) return;
        if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        if (e.ClickCount >= 2)
        {
            _resizeKey = null;
            layout.ResetWidth(key);
            layout.Commit();
            return;
        }
        var i = IndexOf(layout, key);
        if (i < 0) return;
        _resizeKey = key;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = layout.Compute(Bounds.Width).Widths[i];
        e.Pointer.Capture(grip);
    }

    private void OnGripMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeKey == null || Layout is not { } layout) return;
        layout.SetWidth(_resizeKey, _resizeStartWidth + e.GetPosition(this).X - _resizeStartX);
        e.Handled = true;
    }

    private void OnGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_resizeKey == null) return;
        _resizeKey = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        Layout?.Commit();
    }

    // ── Move ──

    private void OnCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressCell = null;
        if (e.Source is Visual v && v.FindAncestorOfType<Border>(includeSelf: true) is { Tag: string } b && _grips.ContainsValue(b))
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Layout is not { } layout) return;
        var cell = DirectChild(e.Source as Visual);
        if (cell == null || layout.Find(GetColumn(cell) ?? string.Empty) is not { Pinned: false }) return;
        _pressCell = cell;
        _pressPoint = e.GetPosition(this);
    }

    private void OnCellPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressCell == null || Layout is not { } layout) return;
        var pos = e.GetPosition(this);
        if (!_moving)
        {
            if (Math.Abs(pos.X - _pressPoint.X) < DragThreshold) return;
            _moving = true;
            // Taking the pointer from the header button cancels its click (no sort on a move).
            e.Pointer.Capture(this);
            _pressCell.Opacity = 0.45;
            _dropMarker.IsVisible = true;
        }
        _dropIndex = DropIndexAt(layout, pos.X);
        InvalidateArrange();
        e.Handled = true;
    }

    private void OnCellPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_moving) { _pressCell = null; return; }
        e.Handled = true;
        EndMove(commit: true);
        e.Pointer.Capture(null);
    }

    private void EndMove(bool commit)
    {
        if (!_moving) { _pressCell = null; return; }
        var cell = _pressCell;
        var index = _dropIndex;
        _moving = false;
        _pressCell = null;
        _dropIndex = -1;
        _dropMarker.IsVisible = false;
        if (cell != null) cell.Opacity = 1;
        if (commit && cell != null && Layout is { } layout && GetColumn(cell) is { } key && index >= 0)
        {
            layout.Move(key, index);
            layout.Commit();
        }
    }

    /// <summary>Slot (0..movable count) whose boundary is nearest <paramref name="x"/>.</summary>
    private int DropIndexAt(ColumnLayout layout, double x)
    {
        var (widths, offsets) = layout.Compute(Bounds.Width);
        var cols = layout.Visible;
        var slot = 0;
        for (var i = 0; i < cols.Count; i++)
        {
            if (cols[i].Pinned) break;
            if (x > offsets[i] + widths[i] / 2) slot = i + 1;
        }
        return slot;
    }

    private static double DropX(ColumnLayout layout, double width, int slot)
    {
        var (widths, offsets) = layout.Compute(width);
        var movable = layout.Visible.Count(s => !s.Pinned);
        if (movable == 0) return 0;
        return slot < movable ? offsets[slot] : offsets[movable - 1] + widths[movable - 1];
    }

    private Control? DirectChild(Visual? v)
    {
        while (v != null && v.GetVisualParent() != this) v = v.GetVisualParent();
        return v is Control c && Children.Contains(c) ? c : null;
    }

    // ── Column menu ──

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Layout is not { } layout) return;
        e.Handled = true;
        BuildMenu(layout).Open(this);
    }

    /// <summary>The header's right-click menu: every column with a tick when shown, then Reset Columns.</summary>
    internal ContextMenu BuildMenu(ColumnLayout layout)
    {
        var menu = new ContextMenu();
        menu.Classes.Add(MenuV2.MenuClass);
        foreach (var spec in layout.Ordered)
        {
            if (spec.Pinned) continue;
            var item = MenuV2.Row(this, spec.Label, layout.IsVisible(spec.Key) ? "MenuLineCheck" : null);
            item.IsEnabled = spec.CanHide;
            item.StaysOpenOnClick = true;
            var key = spec.Key;
            item.Click += (_, _) =>
            {
                layout.RequestVisibility(key, !layout.IsVisible(key));
                item.Icon = layout.IsVisible(key) ? MenuV2.LineIcon(this, "MenuLineCheck") : null;
            };
            AutomationProperties.SetName(item, spec.Label);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = MenuV2.Row(this, Loc.T("Columns.Reset"), "MenuLineHistory");
        reset.Click += (_, _) => { layout.ResetLayout(); layout.Commit(); };
        menu.Items.Add(reset);
        return menu;
    }
}

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
/// <item>a divider at each cell's right edge, always drawn (GitHub #156): drag to resize,
/// double-click for the automatic width;</item>
/// <item>drag anywhere in a header cell sideways to move its column: a lifted copy of the
/// header follows the pointer and an accent bar shows where it lands (GitHub #156);</item>
/// <item>a small arrow by the label of the column the list is sorted by, pointing up for ascending;</item>
/// <item>right-click for a menu of the columns, with ticks, and Reset Columns.</item>
/// </list>
/// A click anywhere in a cell's slot, not just on its label, runs the cell's command (sorting);
/// the slot under the pointer is highlighted so that is visible (GitHub #156).
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
    private readonly Border _hoverBox;
    private readonly Border _ghost;
    private readonly TextBlock _ghostLabel;
    private string? _hoverKey;

    private string? _resizeKey;
    private double _resizeStartX, _resizeStartWidth;

    private Control? _pressCell;
    private Point _pressPoint;
    private bool _pressOnLabel;
    private double _pressCellOpacity = 1;
    private bool _moving;
    private int _dropIndex = -1;
    private double _ghostX;

    /// <summary>The highlight, the lifted header and the drop bar reach this far past the
    /// header's own (text-tall) bounds, so they read as a cell rather than a strip.</summary>
    private const double Bleed = 4;

    /// <summary>Divider strengths: at rest (always drawn, GitHub #156), while the pointer is
    /// over the header, and over / dragging that divider.</summary>
    internal const double DividerRest = 0.3, DividerHeader = 0.5, DividerActive = 1;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    static ColumnHeaderPanel()
    {
        AffectsArrange<ColumnHeaderPanel>(SortKeyProperty, SortAscendingProperty);
    }

    public ColumnHeaderPanel()
    {
        Background = Brushes.Transparent;
        var foreground = this.GetObservable(TextElement.ForegroundProperty);
        // Resources are looked up from the panel itself: these are visual-only children with
        // no logical parent, so a lookup from the bar found nothing and it drew with no brush
        // (GitHub #156: "no visual indication" while moving a column).
        _dropMarker = new Border { Width = 3, IsVisible = false, IsHitTestVisible = false, CornerRadius = new CornerRadius(1.5), ZIndex = 3 };
        _dropMarker.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentColorBrush"));
        VisualChildren.Add(_dropMarker);
        _sortArrow = new Path { Width = 7, Height = 4, Stretch = Stretch.None, IsHitTestVisible = false, Opacity = 0.7, IsVisible = false, ZIndex = 1 };
        // Same colour as the header labels (inherited text foreground); the Fluent brush key it used
        // resolved to nothing here, so the arrow drew with no fill.
        _sortArrow.Bind(Shape.FillProperty, foreground);
        VisualChildren.Add(_sortArrow);

        // The hovered slot: a faint wash of the label colour (reads on light and dark themes),
        // under the cells, so the whole cell shows as the click target.
        _hoverBox = new Border { IsVisible = false, IsHitTestVisible = false, CornerRadius = new CornerRadius(5), Opacity = 0.07, ZIndex = -1 };
        _hoverBox.Bind(Border.BackgroundProperty, foreground);
        VisualChildren.Add(_hoverBox);

        // The column being moved, lifted off the header: page colour (opaque, so it reads over
        // the labels it passes), a label-coloured wash, an accent outline and a shadow.
        _ghostLabel = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var wash = new Border { CornerRadius = new CornerRadius(5), Opacity = 0.1 };
        wash.Bind(Border.BackgroundProperty, foreground);
        _ghost = new Border
        {
            IsVisible = false,
            IsHitTestVisible = false,
            ZIndex = 2,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 14, Color = Color.FromArgb(0x70, 0, 0, 0) }),
            Child = new Panel { Children = { wash, new Border { Padding = new Thickness(8, 0), Child = _ghostLabel } } },
        };
        _ghost.Bind(Border.BackgroundProperty, this.GetResourceObservable("AppMainBackground"));
        _ghost.Bind(Border.BorderBrushProperty, this.GetResourceObservable("AccentColorBrush"));
        VisualChildren.Add(_ghost);

        AddHandler(PointerPressedEvent, OnCellPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnCellPointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnCellPointerReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, (_, _) => EndMove(commit: false));
        ContextRequested += OnContextRequested;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdateDividers();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(null);
        Cursor = null;
        UpdateDividers();
    }

    private void UpdateDividers()
    {
        foreach (var grip in _grips.Values)
            grip.Child!.Opacity = grip.IsPointerOver || Equals(grip.Tag, _resizeKey) ? DividerActive
                : IsPointerOver ? DividerHeader : DividerRest;
    }

    /// <summary>Highlights the slot of <paramref name="key"/> (null: none).</summary>
    private void SetHover(string? key)
    {
        if (_hoverKey == key) return;
        _hoverKey = key;
        _hoverBox.IsVisible = key != null;
        InvalidateArrange();
    }

    /// <summary>The highlighted slot's column, for tests.</summary>
    internal string? HoverKey => _hoverKey;

    /// <summary>The highlight drawn over the hovered slot, for tests.</summary>
    internal Border HoverBox => _hoverBox;

    /// <summary>The lifted header shown while a column moves, for tests.</summary>
    internal Border Ghost => _ghost;

    /// <summary>The bar marking where a moving column lands, for tests.</summary>
    internal Border DropMarker => _dropMarker;

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
                // Above the cells: they join the visual list after the grips, so without this
                // the next column's label covered the right half of the divider's hit area.
                ZIndex = 1,
            };
            // A hairline in the label colour, always drawn so users see where to drag (GitHub
            // #156: the old grey line only showed on hover and was barely visible on dark);
            // stronger while the pointer is over the header, full over the divider itself.
            var line = new Border
            {
                Width = 1,
                Margin = new Thickness(0, 2),
                IsHitTestVisible = false,
                Opacity = IsPointerOver ? DividerHeader : DividerRest,
            };
            line.Bind(Border.BackgroundProperty, this.GetObservable(TextElement.ForegroundProperty));
            grip.Child = line;
            var label = layout!.Find(key)?.Label;
            AutomationProperties.SetName(grip, string.Format(Loc.T("Columns.ResizeName"), label));
            grip.PointerEntered += (_, _) => { SetHover(null); UpdateDividers(); };
            grip.PointerExited += (_, _) => UpdateDividers();
            grip.PointerPressed += OnGripPressed;
            grip.PointerMoved += OnGripMoved;
            grip.PointerReleased += OnGripReleased;
            grip.PointerCaptureLost += (_, _) => { _resizeKey = null; UpdateDividers(); };
            _grips[key] = grip;
            VisualChildren.Insert(VisualChildren.IndexOf(_dropMarker), grip);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        foreach (var grip in _grips.Values) grip.Measure(new Size(GripHalf * 2, size.Height));
        _dropMarker.Measure(new Size(3, size.Height + Bleed * 2));
        _sortArrow.Measure(new Size(7, 4));
        _hoverBox.Measure(Size.Infinity);
        _ghost.Measure(Size.Infinity);
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
            var tall = finalSize.Height + Bleed * 2;
            if (_moving && _dropIndex >= 0)
                _dropMarker.Arrange(new Rect(DropX(layout, finalSize.Width, _dropIndex) - 1.5, -Bleed, 3, tall));
            if (_hoverKey != null && SlotOf(layout, _hoverKey, finalSize.Width) is { } hover)
                _hoverBox.Arrange(new Rect(hover.X, -Bleed, hover.Width, tall));
            if (_moving && _pressCell != null && SlotOf(layout, GetColumn(_pressCell), finalSize.Width) is { } moving)
                _ghost.Arrange(new Rect(_ghostX, -Bleed, moving.Width, tall));
        }
        ArrangeSortArrow();
        return finalSize;
    }

    /// <summary>The slot (full column width, padding included) of <paramref name="key"/>; null when not shown.</summary>
    private static Rect? SlotOf(ColumnLayout layout, string? key, double width)
    {
        var i = IndexOf(layout, key);
        if (i < 0) return null;
        var (widths, offsets) = layout.Compute(width);
        return new Rect(offsets[i], 0, widths[i], 0);
    }

    /// <summary>The movable column whose slot holds <paramref name="x"/>; null over the pinned slot or outside.</summary>
    private string? KeyAt(ColumnLayout layout, double x)
    {
        var (widths, offsets) = layout.Compute(Bounds.Width);
        var cols = layout.Visible;
        for (var i = 0; i < cols.Count; i++)
            if (!cols[i].Pinned && x >= offsets[i] && x < offsets[i] + widths[i]) return cols[i].Key;
        return null;
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
        // 2px clear of the (now always drawn) divider at the slot's right edge.
        x = Math.Max(slotLeft, Math.Min(x, slotRight - 9));
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
        UpdateDividers();
        Layout?.Commit();
    }

    // ── Move ──

    private bool IsGrip(object? source)
        => source is Visual v && v.FindAncestorOfType<Border>(includeSelf: true) is { Tag: string } b && _grips.ContainsValue(b);

    /// <summary>
    /// A press anywhere in a movable column's slot (GitHub #156: not just on the label) arms
    /// a move, and a click there sorts. The slot is found by x, so the empty part of the cell
    /// and the padding between columns count too.
    /// </summary>
    private void OnCellPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressCell = null;
        if (IsGrip(e.Source)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Layout is not { } layout) return;
        var pos = e.GetPosition(this);
        var onCell = DirectChild(e.Source as Visual);
        var cell = onCell ?? (KeyAt(layout, pos.X) is { } key ? CellFor(key) : null);
        if (cell == null || layout.Find(GetColumn(cell) ?? string.Empty) is not { Pinned: false }) return;
        _pressCell = cell;
        _pressPoint = pos;
        // A press on the cell's own button: the button clicks itself. Anywhere else in the slot
        // the release below runs its command.
        _pressOnLabel = onCell is Button;
    }

    private void OnCellPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Layout is not { } layout) return;
        var pos = e.GetPosition(this);
        if (_pressCell == null)
        {
            // Hover: highlight the slot under the pointer and show the hand where a click sorts.
            var key = _resizeKey == null && !IsGrip(e.Source) ? KeyAt(layout, pos.X) : null;
            SetHover(key);
            Cursor = key != null && CellFor(key) is Button ? HandCursor : null;
            return;
        }
        if (!_moving)
        {
            if (Math.Abs(pos.X - _pressPoint.X) < DragThreshold) return;
            _moving = true;
            SetHover(null);
            // Taking the pointer from the header button cancels its click (no sort on a move).
            e.Pointer.Capture(this);
            // Dimmed while it moves, then back to its own opacity: the labels are 0.7, and
            // setting 1 left a moved column's label brighter than the rest.
            _pressCellOpacity = _pressCell.Opacity;
            _pressCell.Opacity = _pressCellOpacity * 0.4;
            var spec = layout.Find(GetColumn(_pressCell) ?? string.Empty);
            _ghostLabel.Text = spec?.Label;
            _ghostLabel.HorizontalAlignment = _pressCell.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Center
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Left;
            _ghost.IsVisible = true;
            _dropMarker.IsVisible = true;
        }
        if (SlotOf(layout, GetColumn(_pressCell), Bounds.Width) is { } slot)
        {
            // The lifted header keeps the spot where it was grabbed under the pointer, within
            // half its width of the header's ends.
            var x = slot.X + pos.X - _pressPoint.X;
            _ghostX = Math.Clamp(x, -slot.Width / 2, Math.Max(-slot.Width / 2, Bounds.Width - slot.Width / 2));
        }
        _dropIndex = DropIndexAt(layout, pos.X);
        InvalidateArrange();
        e.Handled = true;
    }

    private void OnCellPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_moving)
        {
            var cell = _pressCell;
            var onLabel = _pressOnLabel;
            _pressCell = null;
            if (cell is Button { Command: { } command } button && !onLabel && Layout is { } layout
                && e.InitialPressMouseButton == MouseButton.Left
                && KeyAt(layout, e.GetPosition(this).X) == GetColumn(cell)
                && command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
                e.Handled = true;
            }
            return;
        }
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
        _ghost.IsVisible = false;
        if (cell != null) cell.Opacity = _pressCellOpacity;
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

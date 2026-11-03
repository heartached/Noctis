using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Noctis.Helpers;

namespace Noctis.Controls;

/// <summary>
/// Lays its children out as the columns of a shared <see cref="ColumnLayout"/> (GitHub #151):
/// each child names its column with <c>ColumnsPanel.Column</c>, and the panel places it at the
/// column's offset and width, hiding cells of hidden columns. The layout arrives through the
/// inherited <see cref="LayoutProperty"/>, set once on the list, so a row template costs no
/// per-row bindings for its columns and every row reads widths computed once per width.
/// </summary>
public class ColumnsPanel : Panel
{
    /// <summary>The list's column state; inherited, so set it once on an ancestor.</summary>
    public static readonly AttachedProperty<ColumnLayout?> LayoutProperty =
        AvaloniaProperty.RegisterAttached<ColumnsPanel, Control, ColumnLayout?>("Layout", inherits: true);

    /// <summary>The column key a child cell belongs to.</summary>
    public static readonly AttachedProperty<string?> ColumnProperty =
        AvaloniaProperty.RegisterAttached<ColumnsPanel, Control, string?>("Column");

    public static ColumnLayout? GetLayout(Control c) => c.GetValue(LayoutProperty);
    public static void SetLayout(Control c, ColumnLayout? value) => c.SetValue(LayoutProperty, value);
    public static string? GetColumn(Control c) => c.GetValue(ColumnProperty);
    public static void SetColumn(Control c, string? value) => c.SetValue(ColumnProperty, value);

    private ColumnLayout? _subscribed;

    static ColumnsPanel()
    {
        AffectsParentMeasure<ColumnsPanel>(ColumnProperty);
    }

    protected ColumnLayout? Layout => GetLayout(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LayoutProperty) Resubscribe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Resubscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Rows come and go with virtualization; a detached row must not keep listening.
        if (_subscribed != null) _subscribed.Changed -= OnLayoutChanged;
        _subscribed = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Resubscribe()
    {
        var layout = VisualRoot != null ? Layout : null;
        if (ReferenceEquals(layout, _subscribed)) return;
        if (_subscribed != null) _subscribed.Changed -= OnLayoutChanged;
        _subscribed = layout;
        if (layout != null) layout.Changed += OnLayoutChanged;
        OnLayoutChanged(this, EventArgs.Empty);
    }

    protected virtual void OnLayoutChanged(object? sender, EventArgs e)
    {
        ApplyVisibility();
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);
        ApplyVisibility();
    }

    /// <summary>Cells of hidden columns are not drawn or hit-tested; cells without a key always are.</summary>
    private void ApplyVisibility()
    {
        var layout = Layout;
        if (layout == null) return;
        foreach (var child in Children)
        {
            var key = GetColumn(child);
            if (key != null) child.IsVisible = layout.IsVisible(key);
        }
    }

    /// <summary>Index of <paramref name="key"/> in <see cref="ColumnLayout.Visible"/>, or -1.</summary>
    protected static int IndexOf(ColumnLayout layout, string? key)
    {
        if (key == null) return -1;
        var cols = layout.Visible;
        for (var i = 0; i < cols.Count; i++)
            if (cols[i].Key == key) return i;
        return -1;
    }

    /// <summary>Where the cell of shown column <paramref name="i"/> goes: its slot, less the column padding (GitHub #157).</summary>
    protected static Rect CellRect(ColumnLayout layout, int i, double[] widths, double[] offsets, double height)
    {
        var (l, r) = layout.CellInsets(i);
        return new Rect(offsets[i] + l, 0, Math.Max(0, widths[i] - l - r), height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var layout = Layout;
        if (layout == null) return base.MeasureOverride(availableSize);

        var (widths, offsets) = layout.Compute(availableSize.Width);
        double height = 0, right = 0;
        for (var i = 0; i < widths.Length; i++) right = Math.Max(right, offsets[i] + widths[i]);
        foreach (var child in Children)
        {
            var i = IndexOf(layout, GetColumn(child));
            if (i < 0)
            {
                if (GetColumn(child) == null) child.Measure(availableSize);
                continue;
            }
            var (l, r) = layout.CellInsets(i);
            child.Measure(new Size(Math.Max(0, widths[i] - l - r), availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : right;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var layout = Layout;
        if (layout == null) return base.ArrangeOverride(finalSize);

        var (widths, offsets) = layout.Compute(finalSize.Width);
        foreach (var child in Children)
        {
            var key = GetColumn(child);
            var i = IndexOf(layout, key);
            if (i >= 0) child.Arrange(CellRect(layout, i, widths, offsets, finalSize.Height));
            else if (key == null) child.Arrange(new Rect(finalSize));
        }
        return finalSize;
    }
}

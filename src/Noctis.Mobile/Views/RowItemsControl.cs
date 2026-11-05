using Avalonia;
using Avalonia.Controls;

namespace Noctis.Mobile.Views;

/// <summary>
/// An ItemsControl for long virtualized lists whose rows are built once and then only handed the
/// next item.
///
/// Avalonia's own path rebuilt every row that scrolled in. Its containers are ContentPresenters,
/// and its VirtualizingStackPanel takes a recycled container out of the visual and logical trees,
/// gives it the next item and puts it back. A ContentPresenter given new Content while out of the
/// tree drops its child, and one that comes back forgets the template it could have reused, so
/// each row was built again: the Button with its template, the check, the SongRow, cover, mark and
/// every binding. Even a kept row would lose its styles: leaving the logical tree removes every
/// style from every element below it, and coming back matches them all again. On the emulator
/// that was ~10 ms of UI thread per row on the Songs list (frame stall monitor, 11-05), several
/// rows a frame in a fling: the freezes a tester's phone showed three times a second.
///
/// Here the container is a <see cref="RowHost"/> that builds <see cref="ItemsControl.ItemTemplate"/>
/// once and keeps the row as a visual child only. The row's logical parent is this list, which
/// stays in the tree, so its styles stay applied while the panel moves the container: a recycled
/// row only gets the next item as its DataContext (~2 ms a row on the same emulator run). Visuals
/// are the template's, as before.
/// </summary>
public class RowItemsControl : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = DefaultRecycleKey;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new RowHost();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is not RowHost host)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            return;
        }
        if (host.Row is null && ItemTemplate?.Build(item) is { } built)
            host.Row = built;
        if (host.Row is not { } row) return;
        // The row inherits from this list, not from its container, so the item is set on the row
        // itself, and before it joins the tree: its commands must never see the page's DataContext.
        row.DataContext = item;
        if (!ReferenceEquals(row.Parent, this)) LogicalChildren.Add(row);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        // Nothing to undo: the row stays built (and styled) for the next item.
        if (container is not RowHost) base.ClearContainerForItemOverride(container);
    }
}

/// <summary>
/// A <see cref="RowItemsControl"/> container: lays out its row, which is its visual child only
/// (the row's logical parent is the list).
/// </summary>
public sealed class RowHost : Control
{
    private Control? _row;

    public Control? Row
    {
        get => _row;
        internal set
        {
            if (ReferenceEquals(_row, value)) return;
            if (_row != null) VisualChildren.Remove(_row);
            _row = value;
            if (_row != null) VisualChildren.Add(_row);
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_row is null) return default;
        _row.Measure(availableSize);
        return _row.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _row?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

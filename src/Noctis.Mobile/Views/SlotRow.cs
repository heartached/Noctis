using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace Noctis.Mobile.Views;

/// <summary>
/// One row of a grid list (Albums two to a row, Artists three): <see cref="Columns"/> equal
/// slots, each built once from <see cref="ItemTemplate"/> and then only handed the next item.
///
/// The rows were an ItemsControl over a UniformGrid. A recycled row is given a new list, and an
/// ItemsControl over a plain panel answers that by throwing every tile away and building new
/// ones: on the Albums grid every row that scrolled in built both its tiles (Button, AlbumTile,
/// cover, bindings and styles), ~40 ms of layout a row on the emulator, two to three rows a
/// fling frame (frame stall monitor, 11-06). Here the tiles stay and only their DataContext
/// changes, as <see cref="RowItemsControl"/> does for whole rows. A slot with no item (the last
/// row's gap) is hidden with a null DataContext, so it never binds to the row itself.
/// </summary>
public sealed class SlotRow : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<SlotRow, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<SlotRow, IDataTemplate?>(nameof(ItemTemplate));

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<SlotRow, int>(nameof(Columns), 2);

    private readonly List<Control> _slots = new();

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    [InheritDataTypeFromItems(nameof(Items))]
    public IDataTemplate? ItemTemplate
    {
        get => GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemTemplateProperty)
        {
            foreach (var slot in _slots)
            {
                LogicalChildren.Remove(slot);
                VisualChildren.Remove(slot);
            }
            _slots.Clear();
            Fill();
        }
        else if (change.Property == ItemsProperty || change.Property == ColumnsProperty)
        {
            Fill();
        }
    }

    private void Fill()
    {
        var items = Items as IList ?? Items?.Cast<object?>().ToList();
        var columns = Math.Max(1, Columns);
        for (var i = 0; i < columns; i++)
        {
            var item = items != null && i < items.Count ? items[i] : null;
            if (i >= _slots.Count)
            {
                // Built on first need only: a grid whose rows are all full never builds a spare.
                if (item == null || ItemTemplate?.Build(item) is not { } built) break;
                // The item before the tree: a slot's bindings must never see the row as theirs.
                built.DataContext = item;
                _slots.Add(built);
                LogicalChildren.Add(built);
                VisualChildren.Add(built);
                continue;
            }
            var slot = _slots[i];
            slot.DataContext = item;
            slot.IsVisible = item != null;
        }
        for (var i = columns; i < _slots.Count; i++)
        {
            _slots[i].DataContext = null;
            _slots[i].IsVisible = false;
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        var width = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width / columns;
        var slotSize = new Size(width, availableSize.Height);
        double widest = 0, tallest = 0;
        foreach (var slot in _slots)
        {
            slot.Measure(slotSize);
            widest = Math.Max(widest, slot.DesiredSize.Width);
            tallest = Math.Max(tallest, slot.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(width) ? widest * columns : availableSize.Width, tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var width = finalSize.Width / columns;
        for (var i = 0; i < _slots.Count && i < columns; i++)
            _slots[i].Arrange(new Rect(i * width, 0, width, finalSize.Height));
        return finalSize;
    }
}

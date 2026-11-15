using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Noctis.Controls;

/// <summary>
/// A list row's cell for a column that may be hidden: its content is built from
/// <see cref="CellTemplate"/> the first time the cell is measured. A
/// <see cref="ColumnsPanel"/> measures only the cells of shown columns, so a hidden column's cell
/// stays one empty control instead of its content (the Songs page had 9 hidden-by-default
/// columns, 18 of a row's 51 controls, built and styled for every row; 11-15) and is filled when
/// the user shows the column. Laid out like the plain Border it replaces.
/// </summary>
public sealed class LazyCell : Decorator
{
    public static readonly StyledProperty<IDataTemplate?> CellTemplateProperty =
        AvaloniaProperty.Register<LazyCell, IDataTemplate?>(nameof(CellTemplate));

    public IDataTemplate? CellTemplate
    {
        get => GetValue(CellTemplateProperty);
        set => SetValue(CellTemplateProperty, value);
    }

    /// <summary>Whether the content has been built (for tests).</summary>
    internal bool IsBuilt => Child != null;

    protected override Size MeasureOverride(Size availableSize)
    {
        // The content inherits this cell's DataContext (the row's item), as the Border's did.
        if (Child == null && CellTemplate is { } template)
            Child = template.Build(DataContext);
        return base.MeasureOverride(availableSize);
    }
}

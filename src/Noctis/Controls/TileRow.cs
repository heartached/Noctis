using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;

namespace Noctis.Controls;

/// <summary>
/// One row of a tile grid (Albums, Artists, Favorites): <see cref="Items"/> laid out like the
/// UniformGrid the rows used as an ItemsControl panel, one ContentPresenter per item. When the
/// row is handed other items (its list row scrolled off and re-used by
/// <see cref="RetainingVirtualizingStackPanel"/>) the presenters stay and take the new items, so
/// the tiles re-bind. An ItemsControl given a new ItemsSource drops every container and builds
/// the tiles again: about 30 controls each, five to seven a row (11-14, PageScrollBench).
/// A tile's DataContext changes on a re-bind instead of it being Loaded again, so per-tile state
/// (the Ctrl-selection plate) is re-applied on DataContextChanged.
/// </summary>
public sealed class TileRow : UniformGrid
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TileRow, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<IDataTemplate?> ItemTemplateProperty =
        AvaloniaProperty.Register<TileRow, IDataTemplate?>(nameof(ItemTemplate));

    /// <summary>
    /// For items that are not observable (Artist: IsFavorite, ImagePath and CountsLine are
    /// plain properties, shown "when the rows re-materialize"): a tile handed the item it
    /// already shows binds again, so it reads the item's current values. Without it the same
    /// item in the same slot was a no-op, and an artist's tile kept its old favourite star
    /// until its row was re-used for someone else (owner's build 11-27).
    /// </summary>
    public static readonly StyledProperty<bool> RebindSameItemsProperty =
        AvaloniaProperty.Register<TileRow, bool>(nameof(RebindSameItems));

    public bool RebindSameItems { get => GetValue(RebindSameItemsProperty); set => SetValue(RebindSameItemsProperty, value); }

    public IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public IDataTemplate? ItemTemplate { get => GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemTemplateProperty)
            Children.Clear(); // tiles of another template are built afresh
        if (change.Property == ItemsProperty || change.Property == ItemTemplateProperty)
            Sync();
    }

    private void Sync()
    {
        var template = ItemTemplate;
        var i = 0;
        if (Items is { } items)
        {
            foreach (var item in items)
            {
                if (i < Children.Count)
                {
                    // Same template: the presenter keeps its tile, only the item changes.
                    var presenter = (ContentPresenter)Children[i];
                    if (RebindSameItems && ReferenceEquals(presenter.DataContext, item))
                        presenter.DataContext = null;
                    presenter.DataContext = item;
                    presenter.Content = item;
                }
                else
                {
                    // Filled before it joins the tree, as ItemsControl prepares a container: a
                    // tile built in an attached presenter lost its $parent[UserControl]
                    // bindings (no Play command, the cover at its natural size).
                    Children.Add(new ContentPresenter { ContentTemplate = template, DataContext = item, Content = item });
                }
                i++;
            }
        }
        if (i < Children.Count)
            Children.RemoveRange(i, Children.Count - i);
    }
}

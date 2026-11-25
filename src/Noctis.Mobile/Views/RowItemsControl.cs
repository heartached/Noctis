using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;

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

    // A list of mixed rows (an album's disc headers and songs) has no ItemTemplate but a
    // DataTemplate per kind: its containers are kept per kind, so a recycled row is always one
    // built for an item of that kind.
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = ItemTemplate is null && item is not null ? item.GetType() : DefaultRecycleKey;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new RowHost();

    // The rows realised as the list first comes up arrive (Appear); a row realised later, as the
    // list scrolls, was already there before the virtualising panel took it.
    private bool _settled, _settling;

    /// <summary>
    /// A list handed new items (a Reset: Home's Pinned rail after a pin) plays its rows' arrival
    /// again, as a plain panel's rebuilt rows did, though its rows are kept. Off by default: a
    /// list without an arrival has nothing to replay.
    /// </summary>
    public bool ReplaysArrival { get; set; }

    public RowItemsControl()
    {
        ItemsView.CollectionChanged += (_, e) =>
        {
            if (ReplaysArrival && e.Action == NotifyCollectionChangedAction.Reset) _settled = _settling = false;
        };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is not RowHost host)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            return;
        }
        if (!_settling)
        {
            _settling = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _settled = true, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        host.Arrives = !_settled && (host.Row is null || ReplaysArrival);
        if (host.Row is null && (ItemTemplate ?? this.FindDataTemplate(item))?.Build(item) is { } built)
            host.Row = built;
        if (host.Row is not { } row) return;
        // The row inherits from this list, not from its container, so the item is set on the row
        // itself, and before it joins the tree: its commands must never see the page's DataContext.
        row.DataContext = item;
        if (!ReferenceEquals(row.Parent, this)) LogicalChildren.Add(row);
        // A kept row handed a new list's item may already be in the tree, where joining it no
        // longer plays the arrival.
        if (ReplaysArrival && host.Arrives && host.IsAttachedToVisualTree()) Appear.PlayWithin(row);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        // Nothing to undo: the row stays built (and styled) for the next item, which it shows
        // without the arrival fade (the panel may put it back before preparing it).
        if (container is RowHost host) host.Arrives = false;
        else base.ClearContainerForItemOverride(container);
    }
}

/// <summary>
/// A <see cref="RowItemsControl"/> container: lays out its row, which is its visual child only
/// (the row's logical parent is the list).
/// </summary>
public sealed class RowHost : Control
{
    private Control? _row;

    /// <summary>
    /// Whether the row's arrival fade (<see cref="Appear"/>) plays when it joins the tree: only
    /// for a row built as its list first came up. The virtualising panel takes a recycled
    /// container out of the visual tree and puts it back with the next item, so without this a
    /// tile fade (a section's › page) replayed on every row scrolled in, where a plain panel's
    /// tiles, all built at once, never did.
    /// </summary>
    public bool Arrives { get; internal set; } = true;

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

/// <summary>
/// A <see cref="RowItemsControl"/> over a plain (non-virtualizing) panel that keeps the rows of
/// a list it was handed before: a lyric line's words. A kept lyric row is handed the next line,
/// and its word layer then gets that line's words; an ItemsControl over a plain panel answers a
/// new list by dropping every container, so each word cell (texts, glow, bindings, styles) was
/// built again, most of the cost of a word-timed row scrolling in. Dropped containers wait here
/// with their cells built and styled, and the next words take them.
/// </summary>
public sealed class WordItemsControl : RowItemsControl
{
    private readonly Stack<RowHost> _spare = new();

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        _spare.TryPop(out var host) ? host : new RowHost();

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        if (container is RowHost host) _spare.Push(host);
    }
}

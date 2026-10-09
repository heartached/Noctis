using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Noctis.Controls;

/// <summary>
/// A TreeView whose folders fold open and shut (the Folders page) with the sidebar
/// playlist folders' motion instead of snapping. Every level gets a
/// <see cref="FoldingTreeViewItem"/>: a TreeViewItem asks its TreeView for the containers
/// of its own children, so this one override covers nested folders at any depth.
/// </summary>
public class FoldingTreeView : TreeView
{
    protected override Type StyleKeyOverride => typeof(TreeView);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new FoldingTreeViewItem();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
        => NeedsContainer<FoldingTreeViewItem>(item, out recycleKey);
}

/// <summary>
/// A tree row whose child rows fold open and shut on <see cref="FoldingListBoxItem"/>'s
/// timing (<see cref="FoldingListBoxItem.FoldDuration"/> / <see cref="FoldingListBoxItem.FoldEase"/>),
/// with the chevron turning 0-90 degrees off the same value so rows and arrow move as one.
/// </summary>
/// <remarks>
/// The fold is the row's own reported height under a clip, not a re-layout: the stock
/// template (header Border over PART_ItemsPresenter in a StackPanel) is measured and
/// arranged at its natural height every frame, served from the measure cache, and only
/// the height this row reports shrinks. Rows below just move.
///
/// The stock template hides PART_ItemsPresenter the instant IsExpanded goes false. A
/// local IsVisible holds it up through the close (a local value outranks the template
/// binding) and is cleared once the fold lands, so the template takes back over.
///
/// Only a toggle on a loaded row animates. An IsExpanded that arrives with the container
/// (the model's expansion restored when the page is rebuilt on navigating back, or a
/// library refresh rebuilding the tree) lands at rest, already open.
///
/// Only the part of the subtree that can be on screen is animated: the travel is capped
/// at the room between the header and the bottom of the viewport. A folder of hundreds
/// of rows otherwise sweeps its whole height in the same 280 ms and the visible rows
/// snap in within a frame or two. The rest of the subtree joins (or leaves) at the end,
/// off screen.
///
/// The chevron is PART_ExpandCollapseChevron turned about its centre, so the host has to
/// draw it as one right-pointing glyph centred in its box (LibraryFoldersView.axaml), not
/// Fluent's swap between two glyphs.
/// </remarks>
public class FoldingTreeViewItem : TreeViewItem
{
    protected override Type StyleKeyOverride => typeof(TreeViewItem);

    /// <summary>Pixels the child rows glide down from as they open, as the sidebar rows do.</summary>
    private const double Lift = 10;

    /// <summary>A row of slack past the viewport edge, so the end-of-fold join is never on screen.</summary>
    private const double RoomSlack = 32;

    /// <summary>
    /// Most the fold advances in one frame. The first frame of opening a big folder realizes
    /// all its rows; timed off the wall clock, that hitch alone outlasted the whole fold and
    /// the folder snapped open. Capped, a hitch pauses the fold instead of skipping it.
    /// </summary>
    private static readonly TimeSpan MaxFrameStep = TimeSpan.FromMilliseconds(50);

    private ItemsPresenter? _items;
    private Control? _header;
    private Control? _chevron;
    private readonly RotateTransform _turn = new();
    private readonly TranslateTransform _lift = new();
    private readonly RectangleGeometry _topClip = new();

    /// <summary>0 shut, 1 open (eased).</summary>
    private double _reveal;

    /// <summary>Natural (unfolded) size of the row, from the last measure.</summary>
    private Size _natural;

    /// <summary>Header bottom to viewport bottom when the fold started; caps the travel.</summary>
    private double _room = double.PositiveInfinity;

    private bool _folding;
    private double _from;
    private double _to;
    private TimeSpan _elapsed;
    private TimeSpan? _lastFrame;
    private int _generation;

    /// <summary>The eased fold position, 0 shut to 1 open (tests).</summary>
    internal double Reveal => _reveal;

    /// <summary>True while a fold is running (tests).</summary>
    internal bool IsFolding => _folding;

    /// <summary>The chevron's current turn in degrees (tests).</summary>
    internal double ChevronAngle => _turn.Angle;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        SnapTo(IsExpanded);
        base.OnApplyTemplate(e);

        _items = e.NameScope.Find<ItemsPresenter>("PART_ItemsPresenter");
        _header = e.NameScope.Find<Control>("PART_LayoutRoot");
        _chevron = e.NameScope.Find<Control>("PART_ExpandCollapseChevron");
        if (_chevron != null) _chevron.RenderTransform = _turn;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsExpandedProperty)
        {
            var open = change.GetNewValue<bool>();
            if (IsLoaded && _items != null && TopLevel.GetTopLevel(this) is { } top)
                Animate(open ? 1 : 0, top);
            else
                SnapTo(open);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Navigating away mid-fold: come back at rest, not stranded half open.
        SnapTo(IsExpanded);
    }

    private void Animate(double to, TopLevel top)
    {
        _room = RoomBelowHeader();
        BeginFold();
        // From wherever the fold is now, so a click mid-fold turns it around without a jump.
        _from = _reveal;
        _to = to;
        _elapsed = TimeSpan.Zero;
        _lastFrame = null;
        var generation = ++_generation;
        top.RequestAnimationFrame(t => Tick(t, generation, top));
    }

    private void Tick(TimeSpan now, int generation, TopLevel top)
    {
        if (generation != _generation) return;
        if (_lastFrame is { } last)
        {
            var step = now - last;
            _elapsed += step < MaxFrameStep ? step : MaxFrameStep;
        }
        _lastFrame = now;
        var duration = FoldingListBoxItem.FoldDuration;
        var t = duration <= TimeSpan.Zero ? 1 : Math.Clamp(_elapsed / duration, 0, 1);
        SetReveal(_from + (_to - _from) * FoldingListBoxItem.FoldEase.Ease(t));
        if (t < 1) top.RequestAnimationFrame(n => Tick(n, generation, top));
        else SnapTo(IsExpanded);
    }

    /// <summary>Ends any fold and rests the row open or shut, chevron included.</summary>
    private void SnapTo(bool open)
    {
        _generation++;
        _reveal = open ? 1 : 0;
        _turn.Angle = 90 * _reveal;
        _room = double.PositiveInfinity;
        if (!_folding) return;

        _folding = false;
        ClearValue(ClipToBoundsProperty);
        if (_items is { } items)
        {
            items.ClearValue(IsVisibleProperty);
            items.ClearValue(OpacityProperty);
            items.ClearValue(RenderTransformProperty);
            items.ClearValue(ClipProperty);
        }
        InvalidateMeasure();
    }

    private void BeginFold()
    {
        if (_folding) return;
        _folding = true;
        ClipToBounds = true;
        if (_items is { } items)
        {
            items.IsVisible = true;
            _lift.Y = 0;
            items.RenderTransform = _lift;
            items.Clip = _topClip;
        }
        SetReveal(_reveal);
    }

    private void SetReveal(double value)
    {
        _reveal = Math.Clamp(value, 0, 1);
        _turn.Angle = 90 * _reveal;

        if (_items is { } items)
        {
            // Same shaped curves as the sidebar rows: the fade lands early, the slide
            // settles ahead of the height.
            items.Opacity = Noctis.Helpers.Easing.SmootherStep(_reveal / CollapsibleContent.GlideFadeWindow);
            var remaining = 1 - _reveal;
            _lift.Y = -Lift * remaining * remaining;
            // Top edge only (the row's own clip takes the bottom): the rows slide out from
            // under the header rather than over it. Local space, so offset by the slide.
            _topClip.Rect = new Rect(0, -_lift.Y, 1e6, 1e6);
        }
        InvalidateMeasure();
    }

    /// <summary>
    /// Header bottom to the bottom of the scrolling viewport, plus a row of slack.
    /// Unbounded outside a ScrollViewer.
    /// </summary>
    private double RoomBelowHeader()
    {
        if (_header is null || this.FindAncestorOfType<ScrollViewer>() is not { } viewer)
            return double.PositiveInfinity;
        var bottom = _header.TranslatePoint(new Point(0, _header.Bounds.Height), viewer);
        if (bottom is null) return double.PositiveInfinity;
        return Math.Max(0, viewer.Bounds.Height - bottom.Value.Y) + RoomSlack;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_folding) return base.MeasureOverride(availableSize);

        // Natural height every frame (served from the measure cache), reported folded.
        _natural = base.MeasureOverride(availableSize.WithHeight(double.PositiveInfinity));
        var children = _items?.DesiredSize.Height ?? 0;
        var travel = Math.Min(children, _room);
        return new Size(_natural.Width, _natural.Height - children + travel * _reveal);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_folding) return base.ArrangeOverride(finalSize);

        // Full height behind the clip, top-anchored: the rows slide, they don't squash.
        base.ArrangeOverride(finalSize.WithHeight(Math.Max(finalSize.Height, _natural.Height)));
        return finalSize;
    }
}

// Copied from Avalonia: src/Avalonia.Controls/VirtualizingStackPanel.cs, tag 12.1.6
// (commit cf8e564674700348947e93ab19105f27afe859a9), MIT licence, Copyright (c) AvaloniaUI OÜ.
// Why a copy: the recycle path is private (VirtualizingStackPanel.RecycleElement,
// RecycleElementOnItemRemoved, GetRecycledElement are private and non-virtual) and it calls
// VirtualizingPanel.RemoveInternalChild / AddInternalChild (protected, non-virtual), which take a
// recycled row out of the tree. ItemsControl only offers Prepare/ClearContainerForItemOverride and
// ItemContainerGenerator has no virtual members, so no override keeps the row in the tree.
// Deltas from upstream, each marked "// NOCTIS:" below:
//   1. Recycled containers are parked in the tree (Park), re-bound (Rebind) with style
//      transitions off for the re-bind (QuietTransitions / RestoreTransitions).
//   2. IScrollSnapPointsInfo (snap-point properties, events, GetIrregular/RegularSnapPoints and
//      the per-arrange SnapPointsChanged raise) is left out: no Noctis list snaps.
//   3. Avalonia-internal helpers replaced: MathUtilities (nested copy), GetLayoutRoot ->
//      TopLevel.GetTopLevel + UpdateLayout(); "protected internal override" -> "protected override".
//   4. The viewport is re-clipped when the panel grows (OnSizeChanged): upstream clips the effective
//      viewport to Bounds only when the viewport event arrives, so a panel that was still short
//      then kept a short viewport until the next scroll (Albums grid after Carousel -> Grid).
// Re-sync on an Avalonia upgrade: diff the upstream file at the new tag against this one, keep the
// NOCTIS regions. RetainingPanelVersionTests fails until AvaloniaVersionCopiedFrom is updated.

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.VisualTree;

namespace Noctis.Controls
{
    /// <summary>
    /// Avalonia's virtualizing stack panel, except that a row scrolled out of view stays in the
    /// tree, hidden, and is handed the next item that scrolls in. Avalonia's clears it and takes it
    /// out of the tree, so every row scrolled in was built from its item template and styled from
    /// scratch: on the Songs page ~1.3 MB and ~51 controls per 40 px wheel step, the frame budget
    /// gone and a GC every few frames (PageScrollBench, 11-14). Kept, a row only re-binds.
    /// Containers are not cleared when parked, so ContainerClearing is not raised for them and the
    /// next ContainerPrepared arrives for a container already prepared once: handlers must be
    /// idempotent. Parked rows are hidden children; tree walks that look for rows skip !IsVisible.
    /// </summary>
    // NOCTIS: delta 2 (upstream also implements IScrollSnapPointsInfo)
    public class RetainingVirtualizingStackPanel : VirtualizingPanel
    {
        /// <summary>
        /// Defines the <see cref="Orientation"/> property.
        /// </summary>
        public static readonly StyledProperty<Orientation> OrientationProperty =
            StackPanel.OrientationProperty.AddOwner<RetainingVirtualizingStackPanel>();

        /// <summary>
        /// Defines the <see cref="CacheLength"/> property.
        /// </summary>
        public static readonly StyledProperty<double> CacheLengthProperty =
            AvaloniaProperty.Register<RetainingVirtualizingStackPanel, double>(nameof(CacheLength), 0.0,
                validate: v => v is >= 0 and <= 2);

        private static readonly AttachedProperty<object?> RecycleKeyProperty =
            AvaloniaProperty.RegisterAttached<RetainingVirtualizingStackPanel, Control, object?>("RecycleKey");

        private static readonly object s_itemIsItsOwnContainer = new object();
        private readonly Action<Control, int> _recycleElement;
        private readonly Action<Control> _recycleElementOnItemRemoved;
        private readonly Action<Control, int, int> _updateElementIndex;
        private int _scrollToIndex = -1;
        private Control? _scrollToElement;
        private bool _isInLayout;
        private bool _isWaitingForViewportUpdate;
        private double _lastEstimatedElementSizeU = 25;
        private RetainedStackElements? _measureElements;
        private RetainedStackElements? _realizedElements;
        private IScrollAnchorProvider? _scrollAnchorProvider;
        private Rect _viewport;
        private Dictionary<object, Stack<Control>>? _recyclePool;
        private readonly List<Animatable> _quieted = new(); // NOCTIS: delta 1

        /// <summary>NOCTIS: the Avalonia package version this file was copied from (see header).</summary>
        internal const string AvaloniaVersionCopiedFrom = "12.1.6";
        private Control? _focusedElement;
        private int _focusedIndex = -1;
        private Control? _realizingElement;
        private int _realizingIndex = -1;
        private double _bufferFactor;

        private bool _hasReachedStart = false;
        private bool _hasReachedEnd = false;
        private Rect _lastMeasuredExtendedViewport;
        private Rect _lastKnownExtendedViewport;
        private Rect? _lastEffectiveViewport; // NOCTIS: delta 4

        static RetainingVirtualizingStackPanel()
        {
            CacheLengthProperty.Changed.AddClassHandler<RetainingVirtualizingStackPanel>((x, e) => x.OnCacheLengthChanged(e));
        }

        public RetainingVirtualizingStackPanel()
        {
            _recycleElement = RecycleElement;
            _recycleElementOnItemRemoved = RecycleElementOnItemRemoved;
            _updateElementIndex = UpdateElementIndex;

            _bufferFactor = Math.Max(0, CacheLength);
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        }

        /// <summary>
        /// Gets or sets the axis along which items are laid out.
        /// </summary>
        /// <value>
        /// One of the enumeration values that specifies the axis along which items are laid out.
        /// The default is Vertical.
        /// </value>
        public Orientation Orientation
        {
            get => GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }


        /// <summary>
        /// Gets or sets the CacheLength.
        /// </summary>
        /// <remarks>The factor determines how much additional space to maintain above and below the viewport.
        /// A value of 0.5 means half the viewport size will be buffered on each side (up-down or left-right)
        /// This uses more memory as more UI elements are realized, but greatly reduces the number of Measure-Arrange
        /// cycles which can cause heavy GC pressure depending on the complexity of the item layouts.
        /// </remarks>
        public double CacheLength
        {
            get => GetValue(CacheLengthProperty);
            set => SetValue(CacheLengthProperty, value);
        }

        /// <summary>
        /// Gets the index of the first realized element, or -1 if no elements are realized.
        /// </summary>
        public int FirstRealizedIndex => _realizedElements?.FirstIndex ?? -1;

        /// <summary>
        /// Gets the index of the last realized element, or -1 if no elements are realized.
        /// </summary>
        public int LastRealizedIndex => _realizedElements?.LastIndex ?? -1;

        /// <summary>
        /// Returns the viewport that contains any visible elements
        /// </summary>
        internal Rect ViewPort => _viewport;

        /// <summary>
        /// Returns the extended viewport that contains any visible elements and the additional elements for fast scrolling (viewport * CacheLength * 2)
        /// </summary>
        internal Rect LastMeasuredExtendedViewPort => _lastMeasuredExtendedViewport;

        protected override Size MeasureOverride(Size availableSize)
        {
            var items = Items;

            if (items.Count == 0)
                return default;

            var orientation = Orientation;

            // If we're bringing an item into view, ignore any layout passes until we receive a new
            // effective viewport.
            if (_isWaitingForViewportUpdate)
                return EstimateDesiredSize(orientation, items.Count);

            _isInLayout = true;

            try
            {
                _realizedElements?.ValidateStartU(Orientation);
                _realizedElements ??= new();
                _measureElements ??= new();

                // We need to set the lastEstimatedElementSizeU before calling CalculateDesiredSize()
                _ = EstimateElementSizeU();

                // We handle horizontal and vertical layouts here so X and Y are abstracted to:
                // - Horizontal layouts: U = horizontal, V = vertical
                // - Vertical layouts: U = vertical, V = horizontal
                var viewport = CalculateMeasureViewport(orientation, items);

                // If the viewport is disjunct then we can recycle everything.
                if (viewport.viewportIsDisjunct)
                    _realizedElements.RecycleAllElements(_recycleElement);

                // Do the measure, creating/recycling elements as necessary to fill the viewport. Don't
                // write to _realizedElements yet, only _measureElements.
                RealizeElements(items, availableSize, ref viewport);

                // Now swap the measureElements and realizedElements collection.
                (_measureElements, _realizedElements) = (_realizedElements, _measureElements);
                _measureElements.ResetForReuse();

                // If there is a focused element is outside the visible viewport (i.e.
                // _focusedElement is non-null), ensure it's measured.
                _focusedElement?.Measure(availableSize);

                return CalculateDesiredSize(orientation, items.Count, viewport);
            }
            finally
            {
                _isInLayout = false;
            }
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_realizedElements is null)
                return default;

            _isInLayout = true;

            try
            {
                var orientation = Orientation;
                var u = _realizedElements!.StartU;

                // Collection changes before the realized range make its exact position unstable
                // until the next measure. ScrollIntoView can intentionally defer that measure
                // while waiting for an updated viewport, so use the same position estimate used
                // when realizing an element instead of arranging the range at NaN.
                if (double.IsNaN(u))
                    u = GetOrEstimateElementU(_realizedElements.FirstIndex);

                for (var i = 0; i < _realizedElements.Count; ++i)
                {
                    var e = _realizedElements.Elements[i];

                    if (e is not null)
                    {
                        var sizeU = _realizedElements.SizeU[i];
                        var rect = orientation == Orientation.Horizontal ?
                            new Rect(u, 0, sizeU, finalSize.Height) :
                            new Rect(0, u, finalSize.Width, sizeU);

                        e.Arrange(rect);

                        if (e.IsVisible && _viewport.Intersects(rect))
                        {
                            try
                            {
                                _scrollAnchorProvider?.RegisterAnchorCandidate(e);
                            }
                            catch (InvalidOperationException ex)
                            {
                                // Element might have been removed/reparented during virtualization; ignore but log for diagnostics.
                                Logger.TryGet(LogEventLevel.Verbose, LogArea.Layout)?.Log(this,
                                    "RegisterAnchorCandidate ignored for {Element}: not a descendant of ScrollAnchorProvider. {Message}",
                                    e, ex.Message);
                            }
                        }

                        u += orientation == Orientation.Horizontal ? rect.Width : rect.Height;
                    }
                }

                // Ensure that the focused element is in the correct position.
                if (_focusedElement is not null && _focusedIndex >= 0)
                {
                    var realizedEndU = u;
                    var sizeU = orientation == Orientation.Horizontal ?
                        _focusedElement.DesiredSize.Width :
                        _focusedElement.DesiredSize.Height;

                    u = GetOrEstimateElementU(_focusedIndex);

                    // The focused element's position is estimated as it's outside the realized
                    // range. A bad estimate must not place it over the realized elements in the
                    // viewport: an element before the realized range always ends at or before its
                    // start, and one after it always starts at or after its end.
                    if (_realizedElements.Count > 0 && !double.IsNaN(_realizedElements.StartU))
                    {
                        if (_focusedIndex < _realizedElements.FirstIndex)
                            u = Math.Min(u, _realizedElements.StartU - sizeU);
                        else if (_focusedIndex > _realizedElements.LastIndex)
                            u = Math.Max(u, realizedEndU);
                    }

                    var rect = orientation == Orientation.Horizontal ?
                        new Rect(u, 0, sizeU, finalSize.Height) :
                        new Rect(0, u, finalSize.Width, sizeU);

                    _focusedElement.Arrange(rect);
                }

                return finalSize;
            }
            finally
            {
                _isInLayout = false;
                // NOCTIS: delta 2 (upstream raises Horizontal/VerticalSnapPointsChangedEvent here)
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scrollAnchorProvider = this.FindAncestorOfType<IScrollAnchorProvider>();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _scrollAnchorProvider = null;
        }

        protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
        {
            InvalidateMeasure();

            // Always update special elements
            UpdateSpecialElementsOnItemsChanged(e);

            if (_realizedElements is null)
                return;

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    _realizedElements.ItemsInserted(e.NewStartingIndex, e.NewItems!.Count, _updateElementIndex);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    _realizedElements.ItemsRemoved(e.OldStartingIndex, e.OldItems!.Count, _updateElementIndex, _recycleElementOnItemRemoved);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    _realizedElements.ItemsReplaced(e.OldStartingIndex, e.OldItems!.Count, _recycleElementOnItemRemoved);
                    break;
                case NotifyCollectionChangedAction.Move:
                    if (e.OldStartingIndex < 0)
                    {
                        goto case NotifyCollectionChangedAction.Reset;
                    }

                    _realizedElements.ItemsRemoved(e.OldStartingIndex, e.OldItems!.Count, _updateElementIndex, _recycleElementOnItemRemoved);
                    var insertIndex = e.NewStartingIndex;

                    if (e.NewStartingIndex > e.OldStartingIndex)
                    {
                        insertIndex -= e.OldItems!.Count - 1;
                    }

                    _realizedElements.ItemsInserted(insertIndex, e.NewItems!.Count, _updateElementIndex);
                    break;
                case NotifyCollectionChangedAction.Reset:
                    _realizedElements.ItemsReset(_recycleElementOnItemRemoved);
                    break;
            }
        }

        private void UpdateSpecialElementsOnItemsChanged(NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (_focusedElement is not null && e.NewStartingIndex <= _focusedIndex)
                    {
                        var oldIndex = _focusedIndex;
                        _focusedIndex += e.NewItems!.Count;
                        _updateElementIndex(_focusedElement, oldIndex, _focusedIndex);
                    }
                    if (_scrollToElement is not null && e.NewStartingIndex <= _scrollToIndex)
                    {
                        _scrollToIndex += e.NewItems!.Count;
                    }
                    break;
                case NotifyCollectionChangedAction.Remove:
                    if (_focusedElement is not null)
                    {
                        if (e.OldStartingIndex <= _focusedIndex && _focusedIndex < e.OldStartingIndex + e.OldItems!.Count)
                        {
                            RecycleFocusedElement();
                        }
                        else if (e.OldStartingIndex < _focusedIndex)
                        {
                            var oldIndex = _focusedIndex;
                            _focusedIndex -= e.OldItems!.Count;
                            _updateElementIndex(_focusedElement, oldIndex, _focusedIndex);
                        }
                    }
                    if (_scrollToElement is not null)
                    {
                        if (e.OldStartingIndex <= _scrollToIndex && _scrollToIndex < e.OldStartingIndex + e.OldItems!.Count)
                        {
                            RecycleScrollToElement();
                        }
                        else if (e.OldStartingIndex < _scrollToIndex)
                        {
                            _scrollToIndex -= e.OldItems!.Count;
                        }
                    }
                    break;
                case NotifyCollectionChangedAction.Replace:
                    if (_focusedElement is not null && e.OldStartingIndex <= _focusedIndex && _focusedIndex < e.OldStartingIndex + e.OldItems!.Count)
                    {
                        RecycleFocusedElement();
                    }
                    if (_scrollToElement is not null && e.OldStartingIndex <= _scrollToIndex && _scrollToIndex < e.OldStartingIndex + e.OldItems!.Count)
                    {
                        RecycleScrollToElement();
                    }
                    break;
                case NotifyCollectionChangedAction.Move:
                    if (e.OldStartingIndex < 0)
                    {
                        goto case NotifyCollectionChangedAction.Reset;
                    }

                    if (_focusedElement is not null)
                    {
                        if (e.OldStartingIndex <= _focusedIndex && _focusedIndex < e.OldStartingIndex + e.OldItems!.Count)
                        {
                            var oldIndex = _focusedIndex;
                            _focusedIndex = e.NewStartingIndex + (_focusedIndex - e.OldStartingIndex);
                            _updateElementIndex(_focusedElement, oldIndex, _focusedIndex);
                        }
                        else
                        {
                            var newFocusedIndex = _focusedIndex;

                            if (e.OldStartingIndex < _focusedIndex)
                            {
                                newFocusedIndex -= e.OldItems!.Count;
                            }

                            if (e.NewStartingIndex <= newFocusedIndex)
                            {
                                newFocusedIndex += e.NewItems!.Count;
                            }

                            if (newFocusedIndex != _focusedIndex)
                            {
                                var oldIndex = _focusedIndex;
                                _focusedIndex = newFocusedIndex;
                                _updateElementIndex(_focusedElement, oldIndex, _focusedIndex);
                            }
                        }
                    }

                    if (_scrollToElement is not null)
                    {
                        if (e.OldStartingIndex <= _scrollToIndex && _scrollToIndex < e.OldStartingIndex + e.OldItems!.Count)
                        {
                            _scrollToIndex = e.NewStartingIndex + (_scrollToIndex - e.OldStartingIndex);
                        }
                        else
                        {
                            var newScrollToIndex = _scrollToIndex;

                            if (e.OldStartingIndex < _scrollToIndex)
                            {
                                newScrollToIndex -= e.OldItems!.Count;
                            }

                            if (e.NewStartingIndex <= newScrollToIndex)
                            {
                                newScrollToIndex += e.NewItems!.Count;
                            }

                            _scrollToIndex = newScrollToIndex;
                        }
                    }
                    break;
                case NotifyCollectionChangedAction.Reset:
                    if (_focusedElement is not null)
                    {
                        RecycleFocusedElement();
                    }
                    if (_scrollToElement is not null)
                    {
                        RecycleScrollToElement();
                    }
                    break;
            }
        }

        protected override void OnItemsControlChanged(ItemsControl? oldValue)
        {
            base.OnItemsControlChanged(oldValue);

            if (oldValue is not null)
                oldValue.PropertyChanged -= OnItemsControlPropertyChanged;
            if (ItemsControl is not null)
                ItemsControl.PropertyChanged += OnItemsControlPropertyChanged;

            _realizedElements?.ResetForReuse();
            _measureElements?.ResetForReuse();
            if (ItemsControl is not null && _focusedElement is not null)
            {
                RecycleFocusedElement();
            }
            if (ItemsControl is not null && _scrollToElement is not null)
            {
                RecycleScrollToElement();
            }
            if (ItemsControl is null)
            {
                _focusedElement = null;
                _scrollToElement = null;
            }
            _focusedIndex = -1;
            _scrollToIndex = -1;
        }

        protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
        {
            var count = Items.Count;
            var fromControl = from as Control;

            if (count == 0 ||
                (fromControl is null && direction is not NavigationDirection.First and not NavigationDirection.Last))
                return null;

            var horiz = Orientation == Orientation.Horizontal;
            var fromIndex = fromControl != null ? IndexFromContainer(fromControl) : -1;
            var toIndex = fromIndex;

            switch (direction)
            {
                case NavigationDirection.First:
                    toIndex = 0;
                    break;
                case NavigationDirection.Last:
                    toIndex = count - 1;
                    break;
                case NavigationDirection.Next:
                    ++toIndex;
                    break;
                case NavigationDirection.Previous:
                    --toIndex;
                    break;
                case NavigationDirection.Left:
                    if (horiz)
                        --toIndex;
                    break;
                case NavigationDirection.Right:
                    if (horiz)
                        ++toIndex;
                    break;
                case NavigationDirection.Up:
                    if (!horiz)
                        --toIndex;
                    break;
                case NavigationDirection.Down:
                    if (!horiz)
                        ++toIndex;
                    break;
                default:
                    return null;
            }

            if (fromIndex == toIndex)
                return from;

            if (wrap)
            {
                if (toIndex < 0)
                    toIndex = count - 1;
                else if (toIndex >= count)
                    toIndex = 0;
            }

            return ScrollIntoView(toIndex);
        }

        protected override IEnumerable<Control>? GetRealizedContainers()
        {
            return _realizedElements?.Elements.Where(x => x is not null)!;
        }

        protected override Control? ContainerFromIndex(int index)
        {
            if (index < 0 || index >= Items.Count)
                return null;
            if (_scrollToIndex == index)
                return _scrollToElement;
            if (_focusedIndex == index)
                return _focusedElement;
            if (index == _realizingIndex)
                return _realizingElement;
            if (GetRealizedElement(index) is { } realized)
                return realized;
            if (Items[index] is Control c && c.GetValue(RecycleKeyProperty) == s_itemIsItsOwnContainer)
                return c;
            return null;
        }

        protected override int IndexFromContainer(Control container)
        {
            if (container == _scrollToElement)
                return _scrollToIndex;
            if (container == _focusedElement)
                return _focusedIndex;
            if (container == _realizingElement)
                return _realizingIndex;
            return _realizedElements?.GetIndex(container) ?? -1;
        }

        protected override Control? ScrollIntoView(int index)
        {
            var items = Items;

            if (_isInLayout || index < 0 || index >= items.Count || _realizedElements is null || !IsEffectivelyVisible)
                return null;

            if (GetRealizedElement(index) is Control element)
            {
                element.BringIntoView();
                return element;
            }
            else if (TopLevel.GetTopLevel(this) is not null) // NOCTIS: delta 3 (was this.GetLayoutRoot())
            {
                // Create and measure the element to be brought into view. Store it in a field so that
                // it can be re-used in the layout pass.
                var scrollToElement = GetOrCreateElement(items, index);

                scrollToElement.Measure(Size.Infinity);

                // Get the expected position of the element and put it in place.
                var anchorU = GetOrEstimateElementU(index);
                var rect = Orientation == Orientation.Horizontal ?
                    new Rect(anchorU, 0, scrollToElement.DesiredSize.Width, scrollToElement.DesiredSize.Height) :
                    new Rect(0, anchorU, scrollToElement.DesiredSize.Width, scrollToElement.DesiredSize.Height);
                scrollToElement.Arrange(rect);

                // Store the element and index so that they can be used in the layout pass.
                _scrollToElement = scrollToElement;
                _scrollToIndex = index;

                // If the item being brought into view was added since the last layout pass then
                // our bounds won't be updated, so any containing scroll viewers will not have an
                // updated extent. Do a layout pass to ensure that the containing scroll viewers
                // will be able to scroll the new item into view.
                if (!Bounds.Contains(rect) && !_viewport.Contains(rect))
                {
                    _isWaitingForViewportUpdate = true;
                    UpdateLayout(); // NOCTIS: delta 3
                    _isWaitingForViewportUpdate = false;
                }

                // Try to bring the item into view.
                scrollToElement.BringIntoView();

                // If the viewport does not contain the item to scroll to, set _isWaitingForViewportUpdate:
                // this should cause the following chain of events:
                // - Measure is first done with the old viewport (which will be a no-op, see MeasureOverride)
                // - The viewport is then updated by the layout system which invalidates our measure
                // - Measure is then done with the new viewport.
                _isWaitingForViewportUpdate = !_viewport.Contains(rect);
                UpdateLayout(); // NOCTIS: delta 3

                // If for some reason the layout system didn't give us a new viewport during the layout, we
                // need to do another layout pass as the one that took place was a no-op.
                if (_isWaitingForViewportUpdate)
                {
                    _isWaitingForViewportUpdate = false;
                    InvalidateMeasure();
                    UpdateLayout(); // NOCTIS: delta 3
                }

                // During the previous BringIntoView, the scroll width extent might have been out of date if
                // elements have different widths. Because of that, the ScrollViewer might not scroll to the correct offset.
                // After the previous BringIntoView, Y offset should be correct and an extra layout pass has been executed,
                // hence the width extent should be correct now, and we can try to scroll again.
                scrollToElement.BringIntoView();

                // A layout pass normally adopts the temporary element into the realized range and
                // clears _scrollToElement. If it cannot (for example, because the containing pane
                // has no usable width), the element remains an internal child. Recycle only the
                // temporary element created by this call so it cannot remain visible and unindexed.
                if (ReferenceEquals(_scrollToElement, scrollToElement))
                {
                    var scrollToIndex = _scrollToIndex;
                    _scrollToElement = null;
                    _scrollToIndex = -1;
                    RecycleElement(scrollToElement, scrollToIndex);
                    return null;
                }

                return scrollToElement;
            }

            return null;
        }

        internal IReadOnlyList<Control?> GetRealizedElements()
        {
            return _realizedElements?.Elements ?? Array.Empty<Control>();
        }

        private MeasureViewport CalculateMeasureViewport(Orientation orientation, IReadOnlyList<object?> items)
        {
            Debug.Assert(_realizedElements is not null);

            // Use the extended viewport for calculations
            var viewport = _lastMeasuredExtendedViewport;

            // Get the viewport in the orientation direction.
            var viewportStart = orientation == Orientation.Horizontal ? viewport.X : viewport.Y;
            var viewportEnd = orientation == Orientation.Horizontal ? viewport.Right : viewport.Bottom;

            // Get or estimate the anchor element from which to start realization. If we are
            // scrolling to an element, use that as the anchor element. Otherwise, estimate the
            // anchor element based on the current viewport.
            int anchorIndex;
            double anchorU;

            if (_scrollToIndex >= 0 && _scrollToElement is not null)
            {
                anchorIndex = _scrollToIndex;
                anchorU = orientation == Orientation.Horizontal ? _scrollToElement.Bounds.Left : _scrollToElement.Bounds.Top;
            }
            else
            {
                GetOrEstimateAnchorElementForViewport(
                    viewportStart,
                    viewportEnd,
                    items.Count,
                    out anchorIndex,
                    out anchorU);
            }

            // Check if the anchor element is not within the currently realized elements.
            var disjunct = anchorIndex < _realizedElements.FirstIndex ||
                anchorIndex > _realizedElements.LastIndex;

            return new MeasureViewport
            {
                anchorIndex = anchorIndex,
                anchorU = anchorU,
                viewportUStart = viewportStart,
                viewportUEnd = viewportEnd,
                viewportIsDisjunct = disjunct,
            };
        }

        private Size CalculateDesiredSize(Orientation orientation, int itemCount, in MeasureViewport viewport)
        {
            var sizeU = 0.0;
            var sizeV = viewport.measuredV;

            if (viewport.lastIndex >= 0)
            {
                var remaining = itemCount - viewport.lastIndex - 1;
                sizeU = viewport.realizedEndU + (remaining * _lastEstimatedElementSizeU);
            }

            return orientation == Orientation.Horizontal ? new(sizeU, sizeV) : new(sizeV, sizeU);
        }

        private Size EstimateDesiredSize(Orientation orientation, int itemCount)
        {
            if (_scrollToIndex >= 0 && _scrollToElement is not null)
            {
                // We have an element to scroll to, so we can estimate the desired size based on the
                // element's position and the remaining elements.
                var remaining = itemCount - _scrollToIndex - 1;
                var u = orientation == Orientation.Horizontal ?
                    _scrollToElement.Bounds.Right :
                    _scrollToElement.Bounds.Bottom;
                var sizeU = u + (remaining * _lastEstimatedElementSizeU);
                return orientation == Orientation.Horizontal ?
                    new(sizeU, DesiredSize.Height) :
                    new(DesiredSize.Width, sizeU);
            }

            return DesiredSize;
        }

        private double EstimateElementSizeU()
        {
            if (_realizedElements is null)
                return _lastEstimatedElementSizeU;

            var orientation = Orientation;
            var total = 0.0;
            var divisor = 0.0;

            // Average the desired size of the realized, measured elements.
            foreach (var element in _realizedElements.Elements)
            {
                if (element is null || !element.IsMeasureValid)
                    continue;
                var sizeU = orientation == Orientation.Horizontal ?
                    element.DesiredSize.Width :
                    element.DesiredSize.Height;
                total += sizeU;
                ++divisor;
            }

            // Check we have enough information on which to base our estimate.
            if (divisor == 0 || total == 0)
                return _lastEstimatedElementSizeU;

            // Store and return the estimate.
            return _lastEstimatedElementSizeU = total / divisor;
        }

        private void GetOrEstimateAnchorElementForViewport(
            double viewportStartU,
            double viewportEndU,
            int itemCount,
            out int index,
            out double position)
        {
            // We have no elements, or we're at the start of the viewport.
            if (itemCount <= 0 || MathUtilities.IsZero(viewportStartU))
            {
                index = 0;
                position = 0;
                return;
            }

            var horizontal = Orientation == Orientation.Horizontal;
            var viewportEnd = horizontal ? _viewport.Right : _viewport.Bottom;

            if (!_hasReachedEnd && MathUtilities.GreaterThanOrClose(
                viewportEnd,
                horizontal ? Bounds.Width : Bounds.Height))
            {
                index = itemCount - 1;
                position = viewportEnd - EstimateElementSizeU();
                return;
            }

            // If we have realised elements and a valid StartU then try to use this information to
            // get the anchor element.
            if (_realizedElements?.StartU is { } u && !double.IsNaN(u))
            {
                var orientation = Orientation;

                for (var i = 0; i < _realizedElements.Elements.Count; ++i)
                {
                    if (_realizedElements.Elements[i] is not { } element)
                        continue;

                    var sizeU = orientation == Orientation.Horizontal ?
                        element.DesiredSize.Width :
                        element.DesiredSize.Height;
                    var endU = u + sizeU;

                    if (endU > viewportStartU && u < viewportEndU)
                    {
                        index = _realizedElements.FirstIndex + i;
                        position = u;
                        return;
                    }

                    u = endU;
                }
            }

            // We don't have any realized elements in the requested viewport, or can't rely on
            // StartU being valid. Estimate the index using only the estimated element size.
            var estimatedSize = EstimateElementSizeU();

            // Estimate the element at the start of the viewport.
            var startIndex = Math.Min((int)(viewportStartU / estimatedSize), itemCount - 1);
            index = startIndex;
            position = startIndex * estimatedSize;
        }

        private double GetOrEstimateElementU(int index)
        {
            // Return the position of the existing element if realized.
            var u = _realizedElements?.GetElementU(index) ?? double.NaN;

            if (!double.IsNaN(u))
                return u;

            // Estimate the element size.
            var estimatedSize = EstimateElementSizeU();

            // If we have a valid StartU, use it to anchor estimates relative to the realized range.
            if (_realizedElements is { } realized && !double.IsNaN(realized.StartU))
            {
                var first = realized.FirstIndex;
                var last = realized.LastIndex;

                if (index < first)
                {
                    // Interpolate between the known panel origin and the first realized item.
                    // Using the realized items' average size to extrapolate backwards can place
                    // the target before the panel origin.
                    return realized.StartU * index / first;
                }

                if (index > last)
                {
                    var sizes = realized.SizeU;
                    var realizedSpan = 0.0;

                    for (var i = 0; i < sizes.Count; ++i)
                    {
                        var sizeU = sizes[i];
                        realizedSpan += double.IsNaN(sizeU) ? estimatedSize : sizeU;
                    }

                    return realized.StartU + realizedSpan + ((index - last - 1) * estimatedSize);
                }
            }

            return index * estimatedSize;
        }

        private void RealizeElements(
            IReadOnlyList<object?> items,
            Size availableSize,
            ref MeasureViewport viewport)
        {
            Debug.Assert(_measureElements is not null);
            Debug.Assert(_realizedElements is not null);
            Debug.Assert(items.Count > 0);

            var index = viewport.anchorIndex;
            var horizontal = Orientation == Orientation.Horizontal;
            var u = viewport.anchorU;
            var viewportEnd = horizontal ? _viewport.Right : _viewport.Bottom;

            var anchorAtEnd = !_hasReachedEnd && index == items.Count - 1 &&
                !MathUtilities.IsZero(viewport.viewportUStart) &&
                MathUtilities.GreaterThanOrClose(viewportEnd, horizontal ? Bounds.Width : Bounds.Height);

            // Reset boundary flags
            _hasReachedStart = false;
            _hasReachedEnd = false;

            // If the anchor element is at the beginning of, or before, the start of the viewport
            // then we can recycle all elements before it.
            if (u <= viewport.viewportUStart)
                _realizedElements.RecycleElementsBefore(viewport.anchorIndex, _recycleElement);

            // Start at the anchor element and move forwards, realizing elements.
            do
            {
                _realizingIndex = index;
                var e = GetOrCreateElement(items, index);
                _realizingElement = e;

                e.Measure(availableSize);

                var sizeU = horizontal ? e.DesiredSize.Width : e.DesiredSize.Height;
                var sizeV = horizontal ? e.DesiredSize.Height : e.DesiredSize.Width;

                if (anchorAtEnd && index == viewport.anchorIndex)
                {
                    u = viewportEnd - sizeU;
                    viewport.anchorU = u;
                }

                _measureElements!.Add(index, e, u, sizeU);
                viewport.measuredV = Math.Max(viewport.measuredV, sizeV);

                u += sizeU;
                ++index;
                _realizingIndex = -1;
                _realizingElement = null;
            } while (u < viewport.viewportUEnd && index < items.Count);

            // Check if we reached the end of the collection
            _hasReachedEnd = index >= items.Count;

            // Store the last index and end U position for the desired size calculation.
            viewport.lastIndex = index - 1;
            viewport.realizedEndU = u;

            // We can now recycle elements after the last element.
            _realizedElements.RecycleElementsAfter(viewport.lastIndex, _recycleElement);

            // Next move backwards from the anchor element, realizing elements.
            index = viewport.anchorIndex - 1;
            u = viewport.anchorU;

            while (u > viewport.viewportUStart && index >= 0)
            {
                var e = GetOrCreateElement(items, index);

                e.Measure(availableSize);
                var sizeU = horizontal ? e.DesiredSize.Width : e.DesiredSize.Height;
                var sizeV = horizontal ? e.DesiredSize.Height : e.DesiredSize.Width;
                u -= sizeU;

                _measureElements!.Add(index, e, u, sizeU);
                viewport.measuredV = Math.Max(viewport.measuredV, sizeV);
                --index;
            }

            // Check if we reached the start of the collection
            _hasReachedStart = index < 0;

            // We can now recycle elements before the first element.
            _realizedElements.RecycleElementsBefore(index + 1, _recycleElement);
        }

        private Control GetOrCreateElement(IReadOnlyList<object?> items, int index)
        {
            Debug.Assert(ItemContainerGenerator is not null);

            if ((GetRealizedElement(index) ??
                 GetRealizedElement(index, ref _focusedIndex, ref _focusedElement) ??
                 GetRealizedElement(index, ref _scrollToIndex, ref _scrollToElement)) is { } realized)
                return realized;

            var item = items[index];
            var generator = ItemContainerGenerator!;

            if (generator.NeedsContainer(item, index, out var recycleKey))
            {
                return GetRecycledElement(item, index, recycleKey) ??
                       CreateElement(item, index, recycleKey);
            }
            else
            {
                return GetItemAsOwnContainer(item, index);
            }
        }

        private Control? GetRealizedElement(int index)
        {
            return _realizedElements?.GetElement(index);
        }

        private static Control? GetRealizedElement(
            int index,
            ref int specialIndex,
            ref Control? specialElement)
        {
            if (specialIndex == index)
            {
                Debug.Assert(specialElement is not null);

                var result = specialElement;
                specialIndex = -1;
                specialElement = null;
                return result;
            }

            return null;
        }

        private Control GetItemAsOwnContainer(object? item, int index)
        {
            Debug.Assert(ItemContainerGenerator is not null);

            var controlItem = (Control)item!;
            var generator = ItemContainerGenerator!;

            if (!controlItem.IsSet(RecycleKeyProperty))
            {
                generator.PrepareItemContainer(controlItem, controlItem, index);
                AddInternalChild(controlItem);
                controlItem.SetValue(RecycleKeyProperty, s_itemIsItsOwnContainer);
                generator.ItemContainerPrepared(controlItem, item, index);
            }

            controlItem.SetCurrentValue(Visual.IsVisibleProperty, true);
            return controlItem;
        }

        private Control? GetRecycledElement(object? item, int index, object? recycleKey)
        {
            Debug.Assert(ItemContainerGenerator is not null);

            if (recycleKey is null)
                return null;

            var generator = ItemContainerGenerator!;

            if (_recyclePool?.TryGetValue(recycleKey, out var recyclePool) == true && recyclePool.Count > 0)
            {
                var recycled = recyclePool.Pop();
                // NOCTIS: delta 1 (upstream: prepare, AddInternalChild, prepared)
                QuietTransitions(recycled);
                try
                {
                    Rebind(recycled, item);
                    recycled.SetCurrentValue(Visual.IsVisibleProperty, true);
                    generator.PrepareItemContainer(recycled, item, index);
                    generator.ItemContainerPrepared(recycled, item, index);
                }
                finally
                {
                    RestoreTransitions();
                }
                return recycled;
            }

            return null;
        }

        private Control CreateElement(object? item, int index, object? recycleKey)
        {
            Debug.Assert(ItemContainerGenerator is not null);

            var generator = ItemContainerGenerator!;
            var container = generator.CreateContainer(item, index, recycleKey);

            container.SetValue(RecycleKeyProperty, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);

            return container;
        }

        private void RecycleElement(Control element, int index)
        {
            Debug.Assert(ItemsControl is not null);
            Debug.Assert(ItemContainerGenerator is not null);

            _scrollAnchorProvider?.UnregisterAnchorCandidate(element);

            var recycleKey = element.GetValue(RecycleKeyProperty);

            if (recycleKey is null)
            {
                ItemContainerGenerator!.ClearItemContainer(element);
                RemoveInternalChild(element);
            }
            else if (recycleKey == s_itemIsItsOwnContainer)
            {
                element.SetCurrentValue(Visual.IsVisibleProperty, false);
            }
            else if (KeyboardNavigation.GetTabOnceActiveElement(ItemsControl) == element)
            {
                _focusedElement = element;
                _focusedIndex = index;
            }
            else
            {
                Park(recycleKey, element); // NOCTIS: delta 1 (upstream: ClearItemContainer, pool, hide, RemoveInternalChild)
            }
        }

        private void RecycleElementOnItemRemoved(Control element)
        {
            Debug.Assert(ItemContainerGenerator is not null);

            _scrollAnchorProvider?.UnregisterAnchorCandidate(element);

            var recycleKey = element.GetValue(RecycleKeyProperty);

            if (recycleKey is null)
            {
                ItemContainerGenerator!.ClearItemContainer(element);
                RemoveInternalChild(element);
            }
            else if (recycleKey == s_itemIsItsOwnContainer)
            {
                RemoveInternalChild(element);
            }
            else
            {
                Park(recycleKey, element); // NOCTIS: delta 1 (upstream: ClearItemContainer, pool, hide, RemoveInternalChild)
            }
        }

        // NOCTIS: delta 1
        /// <summary>
        /// The one change from Avalonia's panel: a recycled container is hidden and pooled but
        /// stays a child, still bound to its last item. Avalonia clears it (the cleared Content
        /// drops the item template's controls) and removes it (leaving the logical tree drops its
        /// styles), so every row scrolled in was rebuilt and restyled from scratch.
        /// </summary>
        private void Park(object recycleKey, Control element)
        {
            PushToRecyclePool(recycleKey, element);
            element.SetCurrentValue(Visual.IsVisibleProperty, false);
        }

        // NOCTIS: delta 1
        /// <summary>
        /// Hands a parked container its next item. Its old item's selection mark goes first: the
        /// container is not realized, so the ListBox ignores the change (IndexFromContainer is -1),
        /// where the stale mark would otherwise be read back into the selection by the prepare.
        /// The content is set directly (Avalonia's prepare only fills an unset one), so the
        /// ContentPresenter, still in the tree with the same template, keeps its child.
        /// </summary>
        private void Rebind(Control container, object? item)
        {
            if (container.IsSet(SelectingItemsControl.IsSelectedProperty))
                container.ClearValue(SelectingItemsControl.IsSelectedProperty);
            switch (container)
            {
                case ContentControl cc:
                    if (ItemsControl?.ItemTemplate is { } template && cc.ContentTemplate != template)
                        cc.SetCurrentValue(ContentControl.ContentTemplateProperty, template);
                    cc.SetCurrentValue(ContentControl.ContentProperty, item);
                    break;
                case ContentPresenter cp:
                    if (ItemsControl?.ItemTemplate is { } presenterTemplate && cp.ContentTemplate != presenterTemplate)
                        cp.SetCurrentValue(ContentPresenter.ContentTemplateProperty, presenterTemplate);
                    cp.SetCurrentValue(ContentPresenter.ContentProperty, item);
                    break;
            }
        }

        // NOCTIS: delta 1
        /// <summary>
        /// A container Avalonia rebuilt took its item's look at once (it was prepared out of the
        /// tree, where transitions are off). A parked one is rebound in the tree, so for the
        /// rebind its style-set transitions are taken off: a stripe, a rated row's stars or a
        /// selection plate snap to the next item instead of fading across from the last one.
        /// Locally set transitions (controls that manage their own, like HeartIcon) are left alone.
        /// </summary>
        private void QuietTransitions(Visual visual)
        {
            if (visual is Animatable animatable && animatable.Transitions is { Count: > 0 } &&
                animatable.GetDiagnostic(Animatable.TransitionsProperty).Priority > BindingPriority.LocalValue)
            {
                animatable.SetValue(Animatable.TransitionsProperty, null);
                _quieted.Add(animatable);
            }

            foreach (var child in visual.GetVisualChildren())
                QuietTransitions(child);
        }

        // NOCTIS: delta 1
        private void RestoreTransitions()
        {
            foreach (var animatable in _quieted)
                animatable.ClearValue(Animatable.TransitionsProperty);
            _quieted.Clear();
        }

        private void RecycleFocusedElement()
        {
            if (_focusedElement != null)
            {
                RecycleElementOnItemRemoved(_focusedElement);
            }
            _focusedElement = null;
            _focusedIndex = -1;
        }

        private void RecycleScrollToElement()
        {
            if (_scrollToElement != null)
            {
                RecycleElementOnItemRemoved(_scrollToElement);
            }
            _scrollToElement = null;
            _scrollToIndex = -1;
        }

        private void PushToRecyclePool(object recycleKey, Control element)
        {
            _recyclePool ??= new();

            if (!_recyclePool.TryGetValue(recycleKey, out var pool))
            {
                pool = new();
                _recyclePool.Add(recycleKey, pool);
            }

            pool.Push(element);
        }

        private void UpdateElementIndex(Control element, int oldIndex, int newIndex)
        {
            Debug.Assert(ItemContainerGenerator is not null);

            ItemContainerGenerator.ItemContainerIndexChanged(element, oldIndex, newIndex);
        }

        private Rect CalculateExtendedViewport(bool vertical, double viewportSize, double bufferSize)
        {

            var extendedViewportStart = vertical ?
                Math.Max(0, _viewport.Top - bufferSize) :
                Math.Max(0, _viewport.Left - bufferSize);

            var extendedViewportEnd = vertical ?
                Math.Min(Bounds.Height, _viewport.Bottom + bufferSize) :
                Math.Min(Bounds.Width, _viewport.Right + bufferSize);

            // If we are at the start of the list, append 2 * CacheLength additional items
            // If we are at the end of the list, prepend 2 * CacheLength additional items
            // - this way we always maintain "2 * CacheLength * element" items.
            if (vertical)
            {
                var spaceAbove = _viewport.Top - bufferSize;
                var spaceBelow = Bounds.Height - (_viewport.Bottom + bufferSize);

                if (spaceAbove < 0 && spaceBelow >= 0)
                    extendedViewportEnd = Math.Min(Bounds.Height, extendedViewportEnd + Math.Abs(spaceAbove));
                if (spaceAbove >= 0 && spaceBelow < 0)
                    extendedViewportStart = Math.Max(0, extendedViewportStart - Math.Abs(spaceBelow));
            }
            else
            {
                var spaceLeft = _viewport.Left - bufferSize;
                var spaceRight = Bounds.Width - (_viewport.Right + bufferSize);

                if (spaceLeft < 0 && spaceRight >= 0)
                    extendedViewportEnd = Math.Min(Bounds.Width, extendedViewportEnd + Math.Abs(spaceLeft));
                if (spaceLeft >= 0 && spaceRight < 0)
                    extendedViewportStart = Math.Max(0, extendedViewportStart - Math.Abs(spaceRight));
            }

            if (vertical)
            {
                return new Rect(
                    _viewport.X,
                    extendedViewportStart,
                    _viewport.Width,
                    extendedViewportEnd - extendedViewportStart);
            }
            else
            {
                return new Rect(
                    extendedViewportStart,
                    _viewport.Y,
                    extendedViewportEnd - extendedViewportStart,
                    _viewport.Height);
            }
        }

        private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            var vertical = Orientation == Orientation.Vertical;
            var oldViewportStart = vertical ? _viewport.Top : _viewport.Left;
            var oldViewportEnd = vertical ? _viewport.Bottom : _viewport.Right;
            var oldExtendedViewportStart = vertical ? _lastMeasuredExtendedViewport.Top : _lastMeasuredExtendedViewport.Left;
            var oldExtendedViewportEnd = vertical ? _lastMeasuredExtendedViewport.Bottom : _lastMeasuredExtendedViewport.Right;

            // Update current viewport
            _lastEffectiveViewport = e.EffectiveViewport; // NOCTIS: delta 4
            _viewport = e.EffectiveViewport.Intersect(new(Bounds.Size));
            _isWaitingForViewportUpdate = false;

            // Calculate buffer sizes based on viewport dimensions
            var viewportSize = vertical ? _viewport.Height : _viewport.Width;
            var bufferSize = viewportSize * _bufferFactor;

            var extendedViewPort = CalculateExtendedViewport(vertical, viewportSize, bufferSize);

            // Determine if we need a new measure
            var newViewportStart = vertical ? _viewport.Top : _viewport.Left;
            var newViewportEnd = vertical ? _viewport.Bottom : _viewport.Right;
            var newExtendedViewportStart = vertical ? extendedViewPort.Top : extendedViewPort.Left;
            var newExtendedViewportEnd = vertical ? extendedViewPort.Bottom : extendedViewPort.Right;

            var needsMeasure = false;

            // Case 1: Viewport has changed significantly
            if (!MathUtilities.AreClose(oldViewportStart, newViewportStart) ||
                !MathUtilities.AreClose(oldViewportEnd, newViewportEnd))
            {
                // Case 1a: The new viewport exceeds the old extended viewport
                if (newViewportStart < oldExtendedViewportStart ||
                    newViewportEnd > oldExtendedViewportEnd)
                {
                    needsMeasure = true;
                }
                // Case 1b: The extended viewport has changed significantly
                else if (!MathUtilities.AreClose(oldExtendedViewportStart, newExtendedViewportStart) ||
                         !MathUtilities.AreClose(oldExtendedViewportEnd, newExtendedViewportEnd))
                {
                    // Check if we're about to scroll into an area where we don't have realized elements
                    // This would be the case if we're near the edge of our current extended viewport
                    var nearingEdge = false;

                    if (_realizedElements != null)
                    {
                        var firstRealizedElementU = _realizedElements.StartU;
                        var lastRealizedElementU = _realizedElements.StartU;

                        for (var i = 0; i < _realizedElements.Count; i++)
                        {
                            lastRealizedElementU += _realizedElements.SizeU[i];
                        }

                        // If scrolling up/left and nearing the top/left edge of realized elements
                        if (newViewportStart < oldViewportStart &&
                            newViewportStart - newExtendedViewportStart < bufferSize)
                        {
                            // Edge case: We're at item 0 with excess measurement space.
                            // Skip re-measuring since we're at the list start and it won't change the result.
                            // This prevents redundant Measure-Arrange cycles when at list beginning.
                            nearingEdge = !_hasReachedStart;
                        }

                        // If scrolling down/right and nearing the bottom/right edge of realized elements
                        if (newViewportEnd > oldViewportEnd &&
                            newExtendedViewportEnd - newViewportEnd < bufferSize)
                        {
                            // Edge case: We're at the last item with excess measurement space.
                            // Skip re-measuring since we're at the list end and it won't change the result.
                            // This prevents redundant Measure-Arrange cycles when at list beginning.
                            nearingEdge = !_hasReachedEnd;
                        }
                    }
                    else
                    {
                        nearingEdge = true;
                    }

                    needsMeasure = nearingEdge;
                }
            }

            // Supplementary check: detect viewport growth after a previous shrink.
            // The main comparison (Cases 1a/1b) uses _extendedViewport which only updates
            // on measure. When the viewport shrinks (e.g. ComboBox popup during filtering),
            // _extendedViewport stays stale-large, masking subsequent growth. Compare against
            // _lastKnownExtendedViewport (always updated) to catch this case.
            if (!needsMeasure)
            {
                var lastKnownStart = vertical ? _lastKnownExtendedViewport.Top : _lastKnownExtendedViewport.Left;
                var lastKnownEnd = vertical ? _lastKnownExtendedViewport.Bottom : _lastKnownExtendedViewport.Right;
                if (newViewportStart < lastKnownStart || newViewportEnd > lastKnownEnd)
                {
                    needsMeasure = true;
                }
            }

            _lastKnownExtendedViewport = extendedViewPort;

            if (needsMeasure)
            {
                // Only update the measure viewport when triggering a measure. This keeps the
                // wider realization range available for externally-triggered measures (e.g. from
                // OnItemsChanged), ensuring enough items are realized.
                _lastMeasuredExtendedViewport = extendedViewPort;
                InvalidateMeasure();
            }
        }

        // NOCTIS: delta 4. The effective viewport is in the panel's own coordinates, so the panel
        // growing does not raise EffectiveViewportChanged again; but _viewport was clipped to the
        // old, shorter Bounds. Seen on the Albums grid shown again after the Carousel: the panel
        // was arranged 574px tall when the viewport arrived, kept that as its viewport after
        // growing to every row, and the third, partly visible row stayed empty until a scroll.
        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (_lastEffectiveViewport is not { } effective)
                return;
            var clipped = effective.Intersect(new Rect(e.NewSize));
            if (clipped != _viewport)
                OnEffectiveViewportChanged(this, new EffectiveViewportChangedEventArgs(effective));
        }

        private void OnItemsControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_focusedElement is not null &&
                e.Property == KeyboardNavigation.TabOnceActiveElementProperty &&
                e.GetOldValue<IInputElement?>() == _focusedElement)
            {
                // TabOnceActiveElement has moved away from _focusedElement so we can recycle it.
                RecycleElement(_focusedElement, _focusedIndex);
                _focusedElement = null;
                _focusedIndex = -1;
            }
        }

        private void OnCacheLengthChanged(AvaloniaPropertyChangedEventArgs e)
        {
            var newValue = e.GetNewValue<double>();
            _bufferFactor = newValue;

            // Force a recalculation of the extended viewport on the next layout pass
            InvalidateMeasure();
        }

        /// <summary>NOCTIS: delta 3. Avalonia.Utilities.MathUtilities is internal to Avalonia; the three comparisons used here.</summary>
        private static class MathUtilities
        {
            private const double DoubleEpsilon = 2.2204460492503131e-016;

            public static bool AreClose(double value1, double value2)
            {
                if (value1 == value2) return true;
                var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DoubleEpsilon;
                var delta = value1 - value2;
                return -eps < delta && eps > delta;
            }

            public static bool IsZero(double value) => Math.Abs(value) < 10.0 * DoubleEpsilon;

            public static bool GreaterThanOrClose(double value1, double value2) => value1 > value2 || AreClose(value1, value2);
        }

        private struct MeasureViewport
        {
            public int anchorIndex;
            public double anchorU;
            public double viewportUStart;
            public double viewportUEnd;
            public double measuredV;
            public double realizedEndU;
            public int lastIndex;
            public bool viewportIsDisjunct;
        }
    }
}

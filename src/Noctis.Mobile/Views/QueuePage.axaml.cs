using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;
using Noctis.Models;

namespace Noctis.Mobile.Views;

/// <summary>
/// The Queue sheet: it slides up over the page underneath, shows the current track and Up Next
/// over its blurred cover, and floats the transport in a glass pill above the tab bar, which
/// stays up under the sheet (<see cref="TabBar"/>). Up Next rows: drag one by its handle to
/// reorder, swipe it left to remove (spec §5 item 6).
/// Rows are resolved by container index, never by Track: "Add to Queue" of a queued song puts
/// the same Track in the list twice, and IndexOf would act on the first copy. The row's Track
/// is captured too: Up Next can be rebuilt mid-gesture (the track ends, a lock-screen Next), and
/// the release must then not act on whatever row now sits at the pressed index.
/// </summary>
public partial class QueuePage : UserControl
{
    private enum Gesture { None, Pending, Swipe, Drag }

    private Gesture _gesture;
    private Control? _moving;   // the row (swipe) or its container (drag) being translated
    private int _index = -1;
    private Track? _track;      // UpNext[_index] at press
    private Point _start;

    /// <summary>The sheet's slide up and down.</summary>
    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(350);
    private const double PillHeight = 56, PillGap = 12, ListEndGap = 16;

    private ShellViewModel? _vm;
    private bool _shown;        // the sheet is up, or on its way up
    private bool _closing;      // IsQueueOpen dropped; the sheet is sliding away
    private bool _transitionsOn;
    private bool _slidePending; // shown before the sheet joined the visual tree
    private IDisposable? _closeTimer;
    private double _tabBarInset;

    static QueuePage()
    {
        // ShellView binds IsVisible to IsQueueOpen, which drops the moment the queue closes;
        // the sheet still has to slide down. The coercion holds the page up until FinishClose.
        IsVisibleProperty.OverrideMetadata<QueuePage>(
            new StyledPropertyMetadata<bool>(true, coerce: (o, visible) => ((QueuePage)o).CoerceVisible(visible)));
    }

    public QueuePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // The first time the page shows, its content joins the visual tree only in the layout
        // pass after, and a control outside the tree runs no transitions (the sheet just
        // appeared, unanimated): that slide waits for it. It starts once that frame is out,
        // not in it: the first layout of the whole sheet is the slow frame, and a slide started
        // inside it had run most of its course before anything reached the screen.
        Sheet.AttachedToVisualTree += (_, _) =>
        {
            if (_slidePending) Dispatcher.UIThread.Post(() =>
            {
                if (_slidePending) SlideUp();
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>Sliding down after a close: still visible, taking no touches. Internal for tests.</summary>
    internal bool IsClosing => _closing;

    /// <summary>
    /// ShellView's tab bar, which the owner's mockup keeps under the queue: ShellView raises it
    /// over the overlays while this page is on screen. It slides with the sheet, so the two read
    /// as one sheet with the tab bar at its foot; a close that lands on a tab page (a tab
    /// tapped, a page opened) leaves it where it is.
    /// </summary>
    internal Control? TabBar { get; set; }

    /// <summary>How far the tab bar reaches up from the bottom edge, the navigation bar under it
    /// included: the pill and the end of the list stay above it. Kept current by ShellView.</summary>
    internal double TabBarInset
    {
        get => _tabBarInset;
        set
        {
            if (_tabBarInset == value) return;
            _tabBarInset = value;
            ApplySafeArea();
        }
    }

    private bool CoerceVisible(bool visible)
    {
        if (visible)
        {
            // Reopened mid-slide: the page never left the screen, so turn the sheet back up here.
            if (_closing) SlideIn();
            return true;
        }
        if (_closing) return true;
        if (!_shown || TopLevel.GetTopLevel(this) == null) return false;
        BeginClose();
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (change.GetNewValue<bool>()) SlideIn();
        else
        {
            _shown = false;
            ResetGesture();
            UpdateBars();
        }
    }

    /// <summary>Up from below the screen (or back up, from wherever a cancelled close left it).</summary>
    private void SlideIn()
    {
        var reopening = _closing;
        CancelClose();
        _shown = true;
        Sheet.IsHitTestVisible = true;
        if (!reopening)
        {
            // Start below the screen without animating there.
            DetachTransitions();
            Sheet.RenderTransform = Offscreen();
            if (TabBar != null) TabBar.RenderTransform = Offscreen();
            Dim.Opacity = 0;
        }
        UpdateBars();
        if (TopLevel.GetTopLevel(Sheet) == null) _slidePending = true;
        else SlideUp();
    }

    private void SlideUp()
    {
        _slidePending = false;
        AttachTransitions();
        Sheet.RenderTransform = TransformOperations.Parse("translateY(0px)");
        if (TabBar != null) TabBar.RenderTransform = TransformOperations.Parse("translateY(0px)");
        Dim.Opacity = 1;
    }

    /// <summary>Jumps to the end of the running slide. Internal for tests: the headless clock
    /// only moves when a test ticks it.</summary>
    internal void CompleteSlide()
    {
        if (_closing)
        {
            FinishClose();
            return;
        }
        DetachTransitions();
        _slidePending = false;
        Sheet.RenderTransform = TransformOperations.Parse("translateY(0px)");
        if (TabBar != null) TabBar.RenderTransform = TransformOperations.Parse("translateY(0px)");
        Dim.Opacity = 1;
    }

    private void BeginClose()
    {
        _slidePending = false;
        _closing = true;
        Sheet.IsHitTestVisible = false;
        ResetGesture();
        AttachTransitions();
        Sheet.RenderTransform = Offscreen();
        // Back to Now Playing, the tab bar goes down with the sheet (OnShellChanged keeps it in
        // place when the player closes as well).
        if (TabBar != null && _vm?.IsNowPlayingOpen == true) TabBar.RenderTransform = Offscreen();
        Dim.Opacity = 0;
        UpdateBars();
        _closeTimer = DispatcherTimer.RunOnce(FinishClose, Slide);
    }

    /// <summary>The slide has run: let the page hide. Internal for tests, which have no clock.</summary>
    internal void FinishClose()
    {
        CancelClose();
        _shown = false;
        ParkTabBar();
        CoerceValue(IsVisibleProperty);
    }

    /// <summary>The tab bar back in its place, unanimated: once the sheet is gone it drops back
    /// under Now Playing, and must be in place when the player closes.</summary>
    private void ParkTabBar()
    {
        if (TabBar == null) return;
        TabBar.Transitions = null;
        TabBar.RenderTransform = null;
    }

    private void CancelClose()
    {
        _closeTimer?.Dispose();
        _closeTimer = null;
        _closing = false;
    }

    /// <summary>Below the bottom edge: the window's height, which the sheet never exceeds.</summary>
    private ITransform Offscreen()
    {
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height is > 0 and var h ? h : 2000;
        return TransformOperations.Parse($"translateY({height.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
    }

    private void AttachTransitions()
    {
        if (_transitionsOn) return;
        _transitionsOn = true;
        // ShellView's glass bar curve, an iOS sheet's: fast out of the gate, a long soft landing.
        var ease = new CubicBezierEase(0.32, 0.72, 0, 1);
        Sheet.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Slide, Easing = ease },
        };
        Dim.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Slide, Easing = ease } };
        if (TabBar != null)
        {
            TabBar.Transitions = new Transitions
            {
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Slide, Easing = ease },
            };
        }
    }

    private void DetachTransitions()
    {
        _transitionsOn = false;
        Sheet.Transitions = null;
        Dim.Transitions = null;
        if (TabBar != null) TabBar.Transitions = null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnShellChanged;
            _vm.Player.PropertyChanged -= OnPlayerChanged;
        }
        _vm = DataContext as ShellViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnShellChanged;
            _vm.Player.PropertyChanged += OnPlayerChanged;
        }
        ApplySafeArea();
        UpdateBars();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SafeArea)) ApplySafeArea();
        // Closed onto a tab page (CloseNowPlaying drops the Queue first, so BeginClose saw the
        // player still open): the tab bar stays where it is over that page.
        else if (e.PropertyName == nameof(ShellViewModel.IsNowPlayingOpen) && _closing && _vm?.IsNowPlayingOpen == false)
            ParkTabBar();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.IsPlaying)) UpdateBars();
    }

    /// <summary>The pill sits above the tab bar (and the navigation bar under it); the list ends
    /// far enough below its last row for that row to scroll clear of the pill (in landscape the
    /// pill has a column of its own) and the tab bar.</summary>
    private void ApplySafeArea()
    {
        var bottom = Math.Max(_tabBarInset, _vm?.SafeArea.Bottom ?? 0);
        PillHost.Margin = new Thickness(0, 0, 0, bottom);
        var underPill = _landscape == true ? 0 : PillHeight + PillGap;
        QueueScroll.Padding = new Thickness(0, 0, 0, underPill + bottom + ListEndGap);
    }

    private bool? _landscape;

    /// <summary>
    /// Rotation does not recreate the activity, so the sheet re-lays itself out on resize. In a
    /// landscape phone window (~412 dp tall) the header and the pill left Up Next about one row;
    /// side by side, the current track and the pill take the left half and Up Next the right.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyOrientation(e.NewSize.Width > e.NewSize.Height);
    }

    private void ApplyOrientation(bool landscape)
    {
        if (_landscape == landscape) return;
        _landscape = landscape;
        Layout.ColumnDefinitions = landscape ? new ColumnDefinitions("*,*") : new ColumnDefinitions("*");
        Grid.SetColumnSpan(TopBar, landscape ? 2 : 1);
        // Spanning into the star row, the header no longer sizes the rows the list starts under.
        Grid.SetRowSpan(NowRow, landscape ? 3 : 1);
        NowRow.VerticalAlignment = landscape ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        // Side by side the column split divides the header from the list; the rule would only
        // sit over the cover's column.
        HeaderRule.IsVisible = !landscape;
        foreach (var list in new Control[] { EmptyQueue, QueueScroll })
        {
            Grid.SetColumn(list, landscape ? 1 : 0);
            Grid.SetRow(list, landscape ? 1 : 3);
            Grid.SetRowSpan(list, landscape ? 3 : 1);
        }
        ApplySafeArea();
    }

    /// <summary>The equalizer bounces only while a track plays and the sheet is up: a hidden
    /// page keeps its controls, and an animation left running there would keep the app
    /// rendering frames nobody sees.</summary>
    private void UpdateBars() =>
        EqBars.Classes.Set("playing", _vm?.Player.IsPlaying == true && _shown && !_closing);

    private int IndexOf(Control element) =>
        element.FindAncestorOfType<ContentPresenter>() is { } container ? QueueList.IndexFromContainer(container) : -1;

    private Track? TrackAt(int index) =>
        DataContext is ShellViewModel vm && index >= 0 && index < vm.Player.UpNext.Count ? vm.Player.UpNext[index] : null;

    /// <summary>Whether Up Next still holds <paramref name="pressed"/> at <paramref name="index"/>.</summary>
    private bool StillAt(int index, Track? pressed) => pressed != null && ReferenceEquals(TrackAt(index), pressed);

    private void ResetGesture()
    {
        if (_moving != null)
        {
            _moving.RenderTransform = null;
            _moving.ZIndex = 0;
        }
        _moving = null;
        _index = -1;
        _track = null;
        _gesture = Gesture.None;
    }

    // ── Swipe to remove ────────────────────────────────────────────

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || _gesture is Gesture.Swipe or Gesture.Drag) return;
        ResetGesture();   // a press whose release landed elsewhere left a stale Pending
        _moving = row;
        _index = IndexOf(row);
        _track = TrackAt(_index);
        _start = e.GetPosition(this);
        _gesture = Gesture.Pending;
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(sender, _moving) || _gesture is not (Gesture.Pending or Gesture.Swipe)) return;
        var p = e.GetPosition(this);
        var dx = p.X - _start.X;
        if (_gesture == Gesture.Pending)
        {
            // Vertical travel belongs to the ScrollViewer; only a clearly horizontal drag is a swipe.
            if (Math.Abs(dx) < QueueGesture.SlopDip || Math.Abs(dx) <= Math.Abs(p.Y - _start.Y)) return;
            _gesture = Gesture.Swipe;
            e.Pointer.Capture(_moving);
        }
        _moving!.RenderTransform = new TranslateTransform(Math.Min(0, dx), 0);
        e.PreventGestureRecognition();   // a committed swipe keeps its vertical jitter from scrolling
        e.Handled = true;
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(sender, _moving)) return;
        if (_gesture != Gesture.Swipe)
        {
            ResetGesture();
            return;
        }
        var dx = e.GetPosition(this).X - _start.X;
        var width = _moving!.Bounds.Width;
        var (index, track) = (_index, _track);
        ResetGesture();
        CommitSwipe(index, track, dx, width);
        e.Handled = true;
    }

    private void OnRowCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(sender, _moving) && _gesture != Gesture.Drag) ResetGesture();
    }

    /// <summary>The release half of a swipe; a no-op if Up Next no longer holds
    /// <paramref name="pressed"/> at <paramref name="index"/>. Internal for tests.</summary>
    internal void CommitSwipe(int index, Track? pressed, double dx, double rowWidth)
    {
        if (DataContext is ShellViewModel vm && StillAt(index, pressed) && QueueGesture.ShouldRemove(dx, rowWidth))
            vm.Player.RemoveFromQueue(index);
    }

    // ── Drag to reorder (handle) ───────────────────────────────────

    private void OnHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || handle.FindAncestorOfType<ContentPresenter>() is not { } container) return;
        ResetGesture();
        _gesture = Gesture.Drag;
        _moving = container;
        _index = QueueList.IndexFromContainer(container);
        _track = TrackAt(_index);
        _start = e.GetPosition(this);
        container.ZIndex = 1;               // drawn above the rows it passes
        e.Pointer.Capture(handle);
        // The ScrollViewer's touch ScrollGestureRecognizer sees handled events too: once Up Next
        // outgrows the viewport it would take this vertical drag (capture and all) and scroll.
        e.PreventGestureRecognition();
        e.Handled = true;                   // not the start of a swipe on the row
    }

    private void OnHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_gesture != Gesture.Drag || _moving == null) return;
        _moving.RenderTransform = new TranslateTransform(0, e.GetPosition(this).Y - _start.Y);
        e.PreventGestureRecognition();
        e.Handled = true;
    }

    private void OnHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gesture != Gesture.Drag || _moving == null) return;
        var dy = e.GetPosition(this).Y - _start.Y;
        var rowHeight = _moving.Bounds.Height;
        var (from, track) = (_index, _track);
        ResetGesture();
        CommitDrag(from, track, dy, rowHeight);
        e.Handled = true;
    }

    private void OnHandleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_gesture == Gesture.Drag) ResetGesture();
    }

    /// <summary>The release half of a handle drag; a no-op if Up Next no longer holds
    /// <paramref name="pressed"/> at <paramref name="from"/>. Internal for tests.</summary>
    internal void CommitDrag(int from, Track? pressed, double dy, double rowHeight)
    {
        if (DataContext is not ShellViewModel vm || !StillAt(from, pressed)) return;
        var to = QueueGesture.TargetIndex(from, dy, rowHeight, vm.Player.UpNext.Count);
        if (to != from) vm.Player.MoveInQueue(from, to);
    }
}

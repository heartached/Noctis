using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// An album's description, whole (<see cref="ShellViewModel.AlbumNote"/>): the menu's glass card
/// (<see cref="MenuGlass"/>) rising out of the bottom edge with the menu's pop (PopMenu.PopFrom /
/// PopIn / PopOut), the dim behind it fading with it, as the Add to Playlist sheet does. It keeps
/// the note while it closes, so it never shrinks away empty. A swipe down on its top (grabber and
/// header) takes the card with the finger; past <see cref="DismissDistance"/>, or flicked, it drops.
/// </summary>
public partial class AlbumNoteSheet : UserControl
{
    /// <summary>How far a swipe must carry the card, or how fast (dp/ms) it must be flicked, to drop it.</summary>
    internal const double DismissDistance = 110, DismissSpeed = 0.5;
    private const double DragSlop = 10;
    /// <summary>The card stops this far below the status bar, so the page stays in sight above it.</summary>
    internal const double TopRoom = 64;

    private ShellViewModel? _shell;
    // Bumped by every open and close: a deferred step of an older run sees it changed and stops.
    private int _run;

    private bool _pressed, _dragging;
    private Point _dragStart;
    private double _dragLastY, _dragVelocity, _dragOffset;
    private ulong _dragLastTime;

    public AlbumNoteSheet()
    {
        InitializeComponent();
        NoteTop.AddHandler(PointerPressedEvent, OnTopPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnDragCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        SizeChanged += (_, _) => FitHeight();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        Sync();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.AlbumNote)) Sync();
    }

    private void Sync()
    {
        if (_shell?.AlbumNote is { } note)
        {
            if (!IsVisible || !IsHitTestVisible || !ReferenceEquals(NoteBody.DataContext, note)) Open(note);
        }
        else Close();
    }

    /// <summary>The look of the page it opened from: on an album painted in its cover's colour,
    /// the light glass over a light tint and the dark glass over a dark one (the ⋯ menu's rule);
    /// else the app's theme.</summary>
    private ThemeVariant? Theme() =>
        _shell?.CurrentPage is ITintedPage { Tint: { HasTint: true } tint } ? (tint.IsLight ? ThemeVariant.Light : ThemeVariant.Dark) : null;

    /// <summary>The card's tallest: the screen less the status bar and <see cref="TopRoom"/>; a
    /// longer text scrolls inside it.</summary>
    private void FitHeight()
    {
        var height = Bounds.Height;
        if (height <= 0) return;
        NotePop.MaxHeight = Math.Max(240, height - (_shell?.SafeArea.Top ?? 0) - (_shell?.SafeArea.Bottom ?? 0) - TopRoom);
    }

    private void Open(AlbumNoteViewModel note)
    {
        var run = ++_run;
        EndDrag();
        NoteScope.RequestedThemeVariant = Theme();
        NoteBody.DataContext = note;
        NoteScroll.Offset = default;
        IsVisible = true;
        IsHitTestVisible = true;
        FitHeight();
        UpdateLayout();

        PopMenu.PopFrom(NotePop, NoteGlass, NoteBody, new RelativePoint(0.5, 1, RelativeUnit.Relative), PopMenu.SheetPose, Scrim);
        PopMenu.AfterFirstFrame(this, () =>
        {
            if (run != _run) return;
            PopMenu.PopIn(NotePop, NoteGlass, NoteBody, Scrim);
        });
    }

    /// <summary>The pop reversed (or, after a swipe, the card carried on down): inert at once, a
    /// fading card must never take a tap, then gone.</summary>
    private void Close()
    {
        if (!IsVisible) return;
        var run = ++_run;
        var swiped = _dragOffset > 0;
        EndDrag();
        IsHitTestVisible = false;
        PopMenu.PopOut(NotePop, NoteGlass, NoteBody, PopMenu.SheetPose, Scrim);
        if (swiped)
        {
            // Down from where the finger left it, not shrinking back to the bottom edge.
            NotePop.RenderTransform = Motion.TranslateY(NotePop.Bounds.Height + 40);
        }
        DispatcherTimer.RunOnce(() =>
        {
            if (run != _run) return;
            IsVisible = false;
            NoteBody.DataContext = null;
            PopMenu.PopDone(NotePop, NoteGlass, NoteBody, Scrim);
            NotePop.RenderTransform = null;
        }, TimeSpan.FromMilliseconds(PopMenu.PopCloseMs));
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _shell?.CloseSheet();
        e.Handled = true;
    }

    // ---- Swipe down to dismiss -----------------------------------------------------------------

    private void OnTopPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsHitTestVisible) return;
        _pressed = true;
        _dragging = false;
        _dragStart = e.GetPosition(this);
        _dragLastY = _dragStart.Y;
        _dragLastTime = e.Timestamp;
        _dragVelocity = 0;
    }

    private void OnDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        var p = e.GetPosition(this);
        var dy = p.Y - _dragStart.Y;
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) > DragSlop || dy < -DragSlop)
            {
                _pressed = false;   // sideways or up: not a dismiss
                return;
            }
            if (dy <= DragSlop) return;
            _dragging = true;
            e.Pointer.Capture(NoteTop);   // the close button under the finger lets go and does not click
            NotePop.Transitions = null;
        }
        if (e.Timestamp > _dragLastTime)
        {
            _dragVelocity = (p.Y - _dragLastY) / (e.Timestamp - _dragLastTime);
            _dragLastY = p.Y;
            _dragLastTime = e.Timestamp;
        }
        Follow(Math.Max(0, dy));
        e.Handled = true;
    }

    /// <summary>The card <paramref name="offset"/> below its place, the dim thinning as it goes.
    /// Direct sets per move, no transitions (a transition restarted per frame stalls).</summary>
    private void Follow(double offset)
    {
        _dragOffset = offset;
        NotePop.RenderTransform = Motion.TranslateY(offset);
        var height = Math.Max(1, NotePop.Bounds.Height);
        Scrim.Transitions = null;
        Scrim.Opacity = Math.Clamp(1 - offset / height, 0, 1);
    }

    private void OnDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            _pressed = false;
            return;
        }
        var drop = _dragOffset > DismissDistance || _dragVelocity > DismissSpeed;
        e.Pointer.Capture(null);
        e.Handled = true;
        Release(drop);
    }

    private void OnDragCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Taken away mid-drag (the system shade, another gesture): settle back up.
        if (_dragging) Release(drop: false);
    }

    /// <summary>The end of a swipe: drop the card (the shell closes it, from where it is), or
    /// settle it back up.</summary>
    internal void Release(bool drop)
    {
        _pressed = false;
        _dragging = false;
        if (drop && _shell != null)
        {
            _shell.CloseSheet();
            return;
        }
        _dragOffset = 0;
        var settle = TimeSpan.FromMilliseconds(300);
        NotePop.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = settle, Easing = new CubicEaseOut() },
        };
        Scrim.Transitions = new Transitions
        {
            new DoubleTransition { Property = OpacityProperty, Duration = settle, Easing = new CubicEaseOut() },
        };
        NotePop.RenderTransform = TransformOperations.Parse("translateY(0px)");
        Scrim.Opacity = 1;
    }

    /// <summary>For tests: a swipe of <paramref name="offset"/> on the card, without a pointer.</summary>
    internal void FollowForTest(double offset) => Follow(offset);

    private void EndDrag()
    {
        _pressed = false;
        _dragging = false;
        _dragOffset = 0;
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class AddToPlaylistDialog : Window
{
    // ── List ⇄ create pane swap ──
    // Owner 10-08: "add a smooth clean animation when clicking the create new playlist pop up
    // and also when going back". Each side (title, pane, footer) cross-fades with a short
    // horizontal slide — forward to the create form slides left, Back slides right — while the
    // pane area's height eases from the side on screen to the other. Same family as the card:
    // PillDialogHost's eases, through transitions (keyframe animations can't drive
    // RenderTransform), with the hidden pose pinned while they are detached and released next
    // frame. A swap that turns around mid-flight retimes the transitions in place, so every
    // element reverses from where it is; nothing is left half-visible because the settle that
    // collapses the hidden side belongs to the latest swap only.

    /// <summary>How long the incoming side's slide and the height ease take.</summary>
    internal static readonly TimeSpan SwapDuration = TimeSpan.FromMilliseconds(240);
    /// <summary>The incoming side's fade (it lands just before the slide settles).</summary>
    internal static readonly TimeSpan SwapFadeInDuration = TimeSpan.FromMilliseconds(200);
    /// <summary>The outgoing side clears quickly so the two never read as overlapping text.</summary>
    internal static readonly TimeSpan SwapFadeOutDuration = TimeSpan.FromMilliseconds(140);

    internal static readonly TransformOperations SlideRest = TransformOperations.Parse("translateX(0px)");
    internal static readonly TransformOperations SlideLeft = TransformOperations.Parse("translateX(-24px)");
    internal static readonly TransformOperations SlideRight = TransformOperations.Parse("translateX(24px)");

    private sealed class Motion
    {
        public readonly DoubleTransition Fade = new() { Property = OpacityProperty };
        public readonly TransformOperationsTransition Move = new() { Property = RenderTransformProperty };
        public readonly Transitions Transitions;
        public Motion() => Transitions = new Transitions { Fade, Move };
    }

    private readonly Dictionary<Control, Motion> _motions = new();
    private DoubleTransition? _heightEase;
    private Transitions? _heightTransitions;
    private AddToPlaylistDialogViewModel? _vm;
    private bool _showingCreate;
    private bool _modeApplied;
    private int _swapRun;

    /// <summary>True from the start of a pane swap until it has settled (tests).</summary>
    internal bool IsPaneSwapping { get; private set; }

    public AddToPlaylistDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as AddToPlaylistDialogViewModel;
        if (_vm == null) return;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        // Whatever mode the view model starts in is shown as is; only later changes animate.
        ApplyMode(_vm.IsCreatingNew, animate: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnViewModelPropertyChanged;
        ++_swapRun; // a pending settle stands down
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddToPlaylistDialogViewModel.IsCreatingNew) && _vm != null)
            ApplyMode(_vm.IsCreatingNew, animate: IsVisible);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The card's open animation is PillDialogHost's; give the side on screen the keyboard.
        Dispatcher.UIThread.Post(() => FocusSide(_showingCreate), DispatcherPriority.Loaded);
    }

    private Control[] Side(bool create) => create
        ? new Control[] { CreateTitle, CreatePane, CreateFooter }
        : new Control[] { ListTitle, ListPane, ListFooter };

    private Motion MotionOf(Control c)
    {
        if (!_motions.TryGetValue(c, out var m)) _motions[c] = m = new Motion();
        return m;
    }

    /// <summary>The element's transitions, retimed in place for the direction (swapping the
    /// collection mid-flight would snap a reversing element to its target first).</summary>
    private Transitions Timed(Control c, bool incoming)
    {
        var m = MotionOf(c);
        m.Fade.Duration = incoming ? SwapFadeInDuration : SwapFadeOutDuration;
        m.Move.Duration = incoming ? SwapDuration : SwapFadeOutDuration;
        m.Fade.Easing = incoming ? PillDialogHost.OpenEase : PillDialogHost.CloseEase;
        m.Move.Easing = incoming ? PillDialogHost.OpenEase : PillDialogHost.CloseEase;
        return m.Transitions;
    }

    private Transitions HeightTransitions()
    {
        _heightEase ??= new DoubleTransition { Property = HeightProperty, Duration = SwapDuration, Easing = PillDialogHost.OpenEase };
        return _heightTransitions ??= new Transitions { _heightEase };
    }

    private void ApplyMode(bool create, bool animate)
    {
        if (_modeApplied && create == _showingCreate) return;
        _modeApplied = true;
        _showingCreate = create;
        var run = ++_swapRun;
        var incoming = Side(create);
        var outgoing = Side(!create);
        var incomingPane = create ? CreatePane : ListPane;
        var outgoingPane = create ? ListPane : CreatePane;

        if (!animate)
        {
            foreach (var c in incoming)
            {
                c.Transitions = null;
                c.Opacity = 1;
                c.RenderTransform = SlideRest;
                c.IsHitTestVisible = true;
                c.IsVisible = true;
            }
            foreach (var c in outgoing)
            {
                c.Transitions = null;
                c.Opacity = 0;
                c.IsHitTestVisible = false;
                c.IsVisible = false;
            }
            PaneHost.Transitions = null;
            PaneHost.Height = double.NaN;
            ListPane.Height = double.NaN;
            CreatePane.Height = double.NaN;
            IsPaneSwapping = false;
            return;
        }

        IsPaneSwapping = true;
        // Forward (to the form) the new side comes in from the right and the old one leaves
        // to the left; Back mirrors it.
        var enterFrom = create ? SlideRight : SlideLeft;
        var exitTo = create ? SlideLeft : SlideRight;

        // The pane area's height on screen right now, before anything below changes it.
        var fromHeight = PaneHost.Bounds.Height;
        var width = PaneHost.Bounds.Width > 0 ? PaneHost.Bounds.Width : ListPane.Bounds.Width;

        // The outgoing pane keeps its size while it fades (the list's scroller would otherwise
        // shrink and show its bar as the area closes over it).
        if (outgoingPane.IsVisible && outgoingPane.Bounds.Height > 0)
            outgoingPane.Height = outgoingPane.Bounds.Height;

        // A side that is fully hidden starts from its hidden pose, set with no transitions
        // attached; one still on its way out turns around from where it is.
        foreach (var c in incoming)
        {
            if (!c.IsVisible)
            {
                c.Transitions = null;
                c.Opacity = 0;
                c.RenderTransform = enterFrom;
                c.IsVisible = true;
            }
            c.IsHitTestVisible = true;
        }

        // Where the area is heading: the incoming pane at its natural height.
        incomingPane.Height = double.NaN;
        incomingPane.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
        var toHeight = incomingPane.DesiredSize.Height;
        incomingPane.Height = toHeight;

        // From auto, pin the current height without transitions so the ease starts there.
        if (double.IsNaN(PaneHost.Height))
        {
            PaneHost.Transitions = null;
            PaneHost.Height = fromHeight;
        }

        // Out at once; it stops taking clicks before it has faded.
        foreach (var c in outgoing)
        {
            c.Transitions = Timed(c, incoming: false);
            c.IsHitTestVisible = false;
            c.Opacity = 0;
            c.RenderTransform = exitTo;
        }

        // In from the next frame, so the pinned pose is what the transitions start from.
        Dispatcher.UIThread.Post(() =>
        {
            if (run != _swapRun) return;
            foreach (var c in incoming)
            {
                c.Transitions = Timed(c, incoming: true);
                c.Opacity = 1;
                c.RenderTransform = SlideRest;
            }
            PaneHost.Transitions = HeightTransitions();
            PaneHost.Height = toHeight;
            FocusSide(create);
        }, DispatcherPriority.Render);

        _ = SettleSwapAsync(run);
    }

    /// <summary>Once the swap has played: the hidden side collapses and the pane area goes back
    /// to sizing itself. Task.Delay (resumed on the UI thread) as PillDialogHost's close does,
    /// rather than a DispatcherTimer, whose ~15.6 ms tick grid stretches a short wait.</summary>
    private async Task SettleSwapAsync(int run)
    {
        await Task.Delay(SwapDuration + TimeSpan.FromMilliseconds(30));
        if (run != _swapRun) return;
        foreach (var c in Side(!_showingCreate))
        {
            c.IsVisible = false;
            c.IsHitTestVisible = false;
        }
        PaneHost.Transitions = null;
        PaneHost.Height = double.NaN;
        ListPane.Height = double.NaN;
        CreatePane.Height = double.NaN;
        IsPaneSwapping = false;
    }

    private void FocusSide(bool create)
    {
        if (!IsVisible) return;
        if (create) NameTextBox.Focus();
        else CreateNewButton.Focus();
    }

    /// <summary>
    /// Closes the dialog. PillDialogHost turns the close into the animated one (it plays once
    /// however many closes arrive). The caller reads the result from the view model's
    /// PlaylistSelected / NewPlaylistRequested events, so nothing rides on Close(result).
    /// </summary>
    public Task CloseAnimatedAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Cancel does, from either pane.
        if (e.Key == Key.Escape && _vm != null)
        {
            e.Handled = true;
            _vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}

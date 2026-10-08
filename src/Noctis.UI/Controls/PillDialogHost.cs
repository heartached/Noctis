using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls.Metadata;
using Easing = Avalonia.Animation.Easings.Easing;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// The "rounded pill" pop-up shell (owner 10-08: rounded pill design from their reference,
/// metadata window first): a blurred, dimmed copy of the app behind, and a big rounded card
/// with a soft drop shadow that fades/rises/scales in on open and plays the reverse before
/// the window really closes.
///
/// Opt in by making it the root content of a borderless, transparent overlay window that
/// DialogHelper.SizeToOwner-style code sizes over its owner (as every Noctis pop-up already
/// is), with the card's content inside:
/// <code>
/// &lt;controls:PillDialogHost&gt;
///     &lt;Grid Width="852" Height="740"&gt; … &lt;/Grid&gt;
/// &lt;/controls:PillDialogHost&gt;
/// </code>
/// It brings its own styles (Assets/PillDialog.axaml): the template plus the opt-in
/// <c>pill-field</c> / <c>pill-area</c> / <c>pill-primary</c> / <c>pill-secondary</c> classes
/// for whatever sits inside it.
///
/// Closing: every path (a button calling Close(), a view model's CloseRequested, Alt+F4)
/// goes through <see cref="Window.Closing"/>. The first one is cancelled, the close animation
/// plays, then the window is closed for real exactly once; closes requested meanwhile are
/// absorbed by the pending one. App/OS shutdown and an owner closing are never delayed.
/// Window.Close(result) loses its result on the deferred close, so a dialog that needs one
/// keeps it in a property (as RemoveFromLibraryDialog.Choice does).
/// </summary>
[TemplatePart(BackdropLayerPart, typeof(Panel))]
[TemplatePart(BackdropPart, typeof(Image))]
[TemplatePart(CardPart, typeof(Control))]
public class PillDialogHost : ContentControl
{
    private const string BackdropLayerPart = "PART_BackdropLayer";
    private const string BackdropPart = "PART_Backdrop";
    private const string CardPart = "PART_Card";

    /// <summary>Open: backdrop and card fade over this; the card's rise/scale takes
    /// <see cref="OpenMoveDuration"/> on a long ease-out, so it settles just after the fade.</summary>
    public static readonly TimeSpan OpenFadeDuration = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan OpenMoveDuration = TimeSpan.FromMilliseconds(260);
    /// <summary>Close: everything reverses over this, then the window closes.</summary>
    public static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>How long the open waits for the backdrop snapshot before it starts anyway.
    /// The snapshot normally lands within a frame or two; a late one fades in where it is.</summary>
    private static readonly TimeSpan SnapshotWaitBudget = TimeSpan.FromMilliseconds(150);

    // Card pose when hidden: a few px low and slightly small, so it rises and grows in.
    // Both pose strings carry the same operation list so the transition interpolates them.
    private static readonly TransformOperations CardHidden = TransformOperations.Parse("translateY(10px) scale(0.94)");
    private static readonly TransformOperations CardShown = TransformOperations.Parse("translateY(0px) scale(1)");

    // Our own bezier, not Avalonia.SplineEasing (see CubicBezierEase). Same pair the
    // ComboBox drop-down animator uses: a long ease-out in, a short accelerate out.
    // Internal so the Find online panel inside the editor moves on the same curves
    // (owner 10-08: same UI + animation for search metadata).
    internal static readonly Easing OpenEase = new CubicBezierEase(0.16, 1.0, 0.3, 1.0);
    internal static readonly Easing CloseEase = new CubicBezierEase(0.4, 0.0, 1.0, 1.0);

    public static readonly StyledProperty<bool> BlurBackdropProperty =
        AvaloniaProperty.Register<PillDialogHost, bool>(nameof(BlurBackdrop), true);

    /// <summary>Show a blurred copy of the owner window behind the dim (default on). Off, or
    /// without an owner, the dim layer alone sits behind the card.</summary>
    public bool BlurBackdrop
    {
        get => GetValue(BlurBackdropProperty);
        set => SetValue(BlurBackdropProperty, value);
    }

    private Panel? _backdropLayer;
    private Image? _backdrop;
    private Control? _card;
    private Bitmap? _backdropBitmap;
    private Window? _window;

    private DoubleTransition? _backdropFade;
    private DoubleTransition? _cardFade;
    private TransformOperationsTransition? _cardMove;

    private bool _openRequested;
    private bool _closeAnimating;
    private bool _allowClose;
    private bool _closed;

    public PillDialogHost()
    {
        // Self-contained: the template and the pill classes come with the control, so a
        // pop-up opts in with this one element and nothing else to include.
        Styles.Add(new StyleInclude(new Uri("avares://Noctis.UI/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/PillDialog.axaml")
        });

        // Clicks and wheel on the dim area must not fall through to anything (the old
        // overlay Border's job). Bubbling, so content inside the card sees them first.
        AddHandler(PointerPressedEvent, (_, e) => e.Handled = true);
        AddHandler(PointerWheelChangedEvent, (_, e) => e.Handled = true);
        // While the close plays the card is still on screen: a second Save click or Enter
        // there would run the command again (a second tag write), so the card stops taking
        // pointer input (PlayClose) and keys stop at the host.
        AddHandler(KeyDownEvent, (_, e) => { if (_closeAnimating) e.Handled = true; }, RoutingStrategies.Tunnel);
    }

    /// <summary>True while the close animation plays (the window is still open).</summary>
    internal bool IsClosing => _closeAnimating;
    /// <summary>True once the open animation has been started.</summary>
    internal bool IsOpenStarted { get; private set; }
    /// <summary>The blurred owner snapshot currently shown, if any (tests).</summary>
    internal Bitmap? BackdropBitmap => _backdropBitmap;
    internal Control? Card => _card;
    internal Panel? BackdropLayer => _backdropLayer;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _backdropLayer = e.NameScope.Find<Panel>(BackdropLayerPart);
        _backdrop = e.NameScope.Find<Image>(BackdropPart);
        _card = e.NameScope.Find<Control>(CardPart);

        // Hidden pose with no transitions attached, so it is set instantly; the open
        // attaches them and moves to the shown pose (the 2-step pattern RenderTransform
        // needs — keyframe animations can't drive it).
        if (_backdropLayer != null)
        {
            _backdropLayer.Transitions = null;
            _backdropLayer.Opacity = 0;
        }
        if (_card != null)
        {
            _card.Transitions = null;
            _card.Opacity = 0;
            _card.RenderTransformOrigin = RelativePoint.Center;
            _card.RenderTransform = CardHidden;
        }
        if (_backdrop != null && _backdropBitmap != null)
            _backdrop.Source = _backdropBitmap;

        if (_openRequested)
            Dispatcher.UIThread.Post(PlayOpen, DispatcherPriority.Loaded);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is not Window window || ReferenceEquals(window, _window))
            return;
        _window = window;
        window.Opened += OnWindowOpened;
        window.Closing += OnWindowClosing;
        window.Closed += OnWindowClosed;
        // Mounted into a window that is already up: open once the current work is done.
        // Not synchronously — Show() attaches the content while IsVisible is already true
        // but before Owner is set and Opened is raised; Opened then runs first and this
        // posted call finds the open already requested.
        if (window.IsVisible)
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_window, window) && window.IsVisible)
                    OnWindowOpened(window, EventArgs.Empty);
            }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unhook();
        // Whichever comes first, Closed or leaving the tree, frees the snapshot.
        ReleaseBackdrop();
    }

    private void Unhook()
    {
        if (_window is not { } window) return;
        window.Opened -= OnWindowOpened;
        window.Closing -= OnWindowClosing;
        window.Closed -= OnWindowClosed;
        _window = null;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not Window window || _openRequested) return;
        _openRequested = true;

        // Give the snapshot a moment so the blur fades in together with the dim. It never
        // throws (failures leave the dim-only backdrop).
        var capture = CaptureBackdropAsync(window);
        await Task.WhenAny(capture, Task.Delay(SnapshotWaitBudget));
        PlayOpen();
    }

    private void PlayOpen()
    {
        if (_closed || _closeAnimating || _card is null) return;
        IsOpenStarted = true;
        EnsureTransitions();
        SetTiming(OpenFadeDuration, OpenMoveDuration, OpenEase);
        if (_backdropLayer != null) _backdropLayer.Opacity = 1;
        _card.IsHitTestVisible = true;
        _card.Opacity = 1;
        _card.RenderTransform = CardShown;
    }

    private void PlayClose()
    {
        EnsureTransitions();
        SetTiming(CloseDuration, CloseDuration, CloseEase);
        if (_backdropLayer != null) _backdropLayer.Opacity = 0;
        if (_card != null)
        {
            _card.IsHitTestVisible = false;
            _card.Opacity = 0;
            _card.RenderTransform = CardHidden;
        }
    }

    private void EnsureTransitions()
    {
        if (_backdropFade != null) return;
        _backdropFade = new DoubleTransition { Property = OpacityProperty };
        _cardFade = new DoubleTransition { Property = OpacityProperty };
        _cardMove = new TransformOperationsTransition { Property = RenderTransformProperty };
        if (_backdropLayer != null) _backdropLayer.Transitions = new Transitions { _backdropFade };
        if (_card != null) _card.Transitions = new Transitions { _cardFade, _cardMove };
    }

    /// <summary>Retimes the transitions in place. Duration/Easing are read when a value
    /// changes, so this steers the next move without swapping the Transitions collection —
    /// swapping it mid-flight would snap a reversing card to its target first.</summary>
    private void SetTiming(TimeSpan fade, TimeSpan move, Easing easing)
    {
        if (_backdropFade != null) { _backdropFade.Duration = fade; _backdropFade.Easing = easing; }
        if (_cardFade != null) { _cardFade.Duration = fade; _cardFade.Easing = easing; }
        if (_cardMove != null) { _cardMove.Duration = move; _cardMove.Easing = easing; }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose || e.Cancel || sender is not Window window) return;
        // Never hold up shutdown or an owner going away; nothing to animate without a card.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown
                or WindowCloseReason.OwnerWindowClosing
            || _card is null)
            return;

        e.Cancel = true;
        if (_closeAnimating) return; // already on its way out: this request rides along
        _closeAnimating = true;
        PlayClose();
        _ = CloseAfterAnimationAsync(window);
    }

    /// <summary>The real close, once the reverse animation has played. Task.Delay (resumed
    /// on the UI thread) as RemoveFromLibraryDialog does, rather than a DispatcherTimer.</summary>
    private async Task CloseAfterAnimationAsync(Window window)
    {
        await Task.Delay(CloseDuration + TimeSpan.FromMilliseconds(10));
        if (_closed) return;
        _allowClose = true;
        window.Close();
        // Closed can arrive after Close() returns (it is raised by the platform window), but a
        // close that went ahead has already hidden the window; only a vetoed one is visible.
        if (_closed || !window.IsVisible) return;
        // Another Closing handler vetoed the real close: come back rather than leave an
        // invisible window up.
        _allowClose = false;
        _closeAnimating = false;
        PlayOpen();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        Unhook();
        ReleaseBackdrop();
    }

    private void ReleaseBackdrop()
    {
        if (_backdrop != null) _backdrop.Source = null;
        _backdropBitmap?.Dispose();
        _backdropBitmap = null;
    }

    /// <summary>
    /// Blurred copy of the owner window (BackdropSnapshot), laid exactly over where the
    /// owner sits.
    /// </summary>
    private async Task CaptureBackdropAsync(Window window)
    {
        if (!BlurBackdrop || window.Owner is not TopLevel owner) return;
        var bitmap = await BackdropSnapshot.CaptureBlurredAsync(owner, owner, "PillDialog.Backdrop");
        if (bitmap is null) return;
        if (_closed || _backdrop is null) { bitmap.Dispose(); return; }

        // Where the owner's client area sits inside this window. The overlay can extend
        // over the owner's title bar (DialogHelper.SizeToOwner on Windows) or a maximized
        // window's working area, so the offset is measured, not assumed to be zero.
        var size = owner.ClientSize;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var ownerOrigin = owner.PointToScreen(new Point(0, 0));
        var hostOrigin = this.PointToScreen(new Point(0, 0));
        _backdrop.Margin = new Thickness(
            (ownerOrigin.X - hostOrigin.X) / scaling,
            (ownerOrigin.Y - hostOrigin.Y) / scaling, 0, 0);
        _backdrop.Width = size.Width;
        _backdrop.Height = size.Height;

        _backdropBitmap?.Dispose();
        _backdropBitmap = bitmap;
        _backdrop.Source = bitmap;
    }
}

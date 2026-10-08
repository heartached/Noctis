using System;
using System.IO;
using System.Runtime.InteropServices;
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
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
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

    /// <summary>The owner is rendered at a quarter of its layout size: the blur throws that
    /// detail away anyway, and it keeps the snapshot and the blur pass a few ms.</summary>
    private const double SnapshotScaling = 0.25;
    /// <summary>Box radius in snapshot pixels; three passes ≈ a gaussian of σ ≈ 4.5 px,
    /// i.e. ~18 layout px once the quarter-size image is stretched back.</summary>
    private const int BlurRadius = 4;
    private const int BlurPasses = 3;

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
    /// Blurred copy of the owner window, laid exactly over where the owner sits.
    ///
    /// Why a snapshot rather than an OS blur: an AcrylicBlur/Blur hint on a borderless
    /// transparent window painted the whole owner black on Win32 (09-07), a frosted window
    /// region left the Mini Player's edges jagged, and an in-window GlassPanel only sees its
    /// own window's surface. The owner is rendered once by its own compositor (render
    /// thread, its own GPU context — a UI-thread RenderTargetBitmap of it would run the
    /// Liquid Glass backdrop ops off their thread and against a foreign surface), at a
    /// quarter size, then box-blurred once off the UI thread. After that it is a plain
    /// bitmap draw: nothing re-blurs per frame during the fades.
    /// </summary>
    private async Task CaptureBackdropAsync(Window window)
    {
        if (!BlurBackdrop || window.Owner is not TopLevel owner) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var visual = ElementComposition.GetElementVisual(owner);
            var size = owner.ClientSize;
            if (visual is null || size.Width < 1 || size.Height < 1) return;

            byte[] pixels;
            int w, h;
            PixelFormat format;
            AlphaFormat alpha;
            using (var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, SnapshotScaling))
            using (var png = new MemoryStream())
            {
                // The snapshot is a render-target bitmap, which can't CopyPixels ("not
                // supported for this bitmap type"); a PNG round-trip of the quarter-size
                // image (~1 ms) gives a decoded bitmap that can, on every backend. Same
                // route DominantColorExtractor takes for its RenderTargetBitmaps.
                snapshot.Save(png, PngBitmapEncoderOptions.Default);
                png.Position = 0;
                using var raw = new Bitmap(png);
                w = raw.PixelSize.Width;
                h = raw.PixelSize.Height;
                if (w < 1 || h < 1) return;
                format = raw.Format ?? PixelFormat.Bgra8888;
                alpha = raw.AlphaFormat ?? AlphaFormat.Premul;
                pixels = new byte[w * h * 4];
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { raw.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), pixels.Length, w * 4); }
                finally { handle.Free(); }
            }

            // Owner 10-08: white specks on the pill backdrop. This window is transparent and
            // sits over the real owner, so any snapshot pixel below alpha 255 (a translucent
            // owner surface — Liquid Glass paints the window root at 35% — or whatever a GPU
            // pass leaves half-covered) stayed see-through after the blur, and the sharp owner
            // showed through it: crisp white icon and text fragments over the blur. Laid over
            // the owner's own opaque background first, the blurred copy is opaque everywhere
            // (a blur of alpha-255 pixels stays 255) and nothing behind it can show.
            var baseColor = OpaqueBaseColor(owner);
            var rgba = format == PixelFormat.Rgba8888;
            var premultiplied = alpha != AlphaFormat.Unpremul;
            await Task.Run(() =>
            {
                FlattenOpaque(pixels, baseColor, rgba, premultiplied);
                BoxBlur(pixels, w, h, BlurRadius, BlurPasses);
            });

            if (_closed || _backdrop is null) return;
            var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), format, alpha);
            using (var fb = bitmap.Lock())
            {
                for (var y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            }

            // Where the owner's client area sits inside this window. The overlay can extend
            // over the owner's title bar (DialogHelper.SizeToOwner on Windows) or a maximized
            // window's working area, so the offset is measured, not assumed to be zero.
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
        catch (Exception ex)
        {
            // Some backends can't snapshot or read back; the dim alone is a fine backdrop.
            DebugLogger.Warn(DebugLogger.Category.UI, "PillDialog.Backdrop", ex.Message);
        }
    }

    /// <summary>The colour the snapshot is flattened onto: the owner's own background made
    /// opaque (with Liquid Glass on it is the theme surface at 35%, so its colour is the
    /// surface the glass tints toward), else black/white by theme.</summary>
    private static Color OpaqueBaseColor(TopLevel owner)
    {
        switch (owner.Background)
        {
            case ISolidColorBrush solid:
                return Color.FromRgb(solid.Color.R, solid.Color.G, solid.Color.B);
            case IGradientBrush { GradientStops.Count: > 0 } gradient:
                // Some themes paint the window root with a gradient (Smoke): its average.
                int r = 0, g = 0, b = 0, n = gradient.GradientStops.Count;
                foreach (var stop in gradient.GradientStops) { r += stop.Color.R; g += stop.Color.G; b += stop.Color.B; }
                return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            default:
                return owner.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? Colors.White : Colors.Black;
        }
    }

    /// <summary>
    /// Lays every pixel over the opaque <paramref name="baseColor"/>, in place, so the result
    /// has alpha 255 everywhere and no part of the backdrop is see-through (owner 10-08:
    /// white specks on the pill backdrop — the dialog is a transparent window over the real
    /// owner, so a snapshot pixel below 255 lets the sharp owner show through the blur).
    /// Fully opaque pixels are untouched. <paramref name="rgba"/> picks the channel order
    /// (else BGRA); <paramref name="premultiplied"/> says how the colour bytes are stored.
    /// </summary>
    internal static void FlattenOpaque(byte[] pixels, Color baseColor, bool rgba, bool premultiplied)
    {
        int c0 = rgba ? baseColor.R : baseColor.B, c1 = baseColor.G, c2 = rgba ? baseColor.B : baseColor.R;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;
            var rest = 255 - a;
            if (premultiplied)
            {
                // src-over onto an opaque base: c + base·(1 − a). Clamped, as a bad premultiplied
                // pixel (colour above its alpha) would otherwise wrap.
                pixels[i] = (byte)Math.Min(255, pixels[i] + (c0 * rest + 127) / 255);
                pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + (c1 * rest + 127) / 255);
                pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + (c2 * rest + 127) / 255);
            }
            else
            {
                pixels[i] = (byte)((pixels[i] * a + c0 * rest + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * a + c1 * rest + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * a + c2 * rest + 127) / 255);
            }
            pixels[i + 3] = 255;
        }
    }

    /// <summary>
    /// <paramref name="passes"/> box blurs of radius <paramref name="radius"/> (together ≈ a
    /// gaussian), clamp-to-edge, in place. Every byte channel is blurred independently, so it
    /// is channel-order agnostic, and premultiplied pixels stay valid.
    /// </summary>
    internal static void BoxBlur(byte[] pixels, int w, int h, int radius, int passes)
    {
        if (radius < 1 || w < 1 || h < 1) return;
        var tmp = new byte[pixels.Length];
        for (var p = 0; p < passes; p++)
        {
            BoxPass(pixels, tmp, w, h, radius, 4, w * 4);   // horizontal: step one pixel
            BoxPass(tmp, pixels, h, w, radius, w * 4, 4);   // vertical: step one row
        }
    }

    /// <summary>One running-sum box pass along <paramref name="count"/> pixels spaced
    /// <paramref name="step"/> bytes apart, for each of <paramref name="lines"/> lines spaced
    /// <paramref name="lineStep"/> bytes apart.</summary>
    private static void BoxPass(byte[] src, byte[] dst, int count, int lines, int radius, int step, int lineStep)
    {
        var window = 2 * radius + 1;
        var last = count - 1;
        for (var line = 0; line < lines; line++)
        {
            var origin = line * lineStep;
            for (var c = 0; c < 4; c++)
            {
                var basis = origin + c;
                var sum = 0;
                for (var i = -radius; i <= radius; i++)
                    sum += src[basis + Math.Clamp(i, 0, last) * step];
                for (var x = 0; x < count; x++)
                {
                    dst[basis + x * step] = (byte)((sum + window / 2) / window);
                    sum += src[basis + Math.Min(x + radius + 1, last) * step]
                         - src[basis + Math.Max(x - radius, 0) * step];
                }
            }
        }
    }
}

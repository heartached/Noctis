using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Converters;
using Noctis.Mobile.ViewModels;
using Noctis.Services;

namespace Noctis.Mobile.Views;

/// <summary>
/// Settings › Appearance › Now Playing Background › Blurred Cover (Discord #mobile-beta): the
/// playing cover, blurred, filling the screen behind Now Playing and its Lyrics. Never a live blur
/// effect: once per cover (and once per Blur step), off the UI thread, the cover's 128 px copy is
/// blurred into a small bitmap (<see cref="PreBlurredArtworkConverter.BlurredCopy"/>), which is
/// then drawn stretched, a plain bitmap draw like the wash's. A new cover cross-fades over the old
/// one (<see cref="FadeDuration"/>; at once under Battery Saver), and the old bitmap is disposed.
/// Draws nothing, and says so (<see cref="HasImage"/>), while off or without a cover: the page
/// shows its wash then.
/// </summary>
public sealed class BlurredCoverBackdrop : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<BlurredCoverBackdrop, IImage?>(nameof(Source));

    public static readonly StyledProperty<int> BlurPercentProperty =
        AvaloniaProperty.Register<BlurredCoverBackdrop, int>(nameof(BlurPercent), 150);

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<BlurredCoverBackdrop, bool>(nameof(IsActive));

    public static readonly DirectProperty<BlurredCoverBackdrop, bool> HasImageProperty =
        AvaloniaProperty.RegisterDirect<BlurredCoverBackdrop, bool>(nameof(HasImage), o => o.HasImage);

    public static readonly StyledProperty<double> DimProperty =
        AvaloniaProperty.Register<BlurredCoverBackdrop, double>(nameof(Dim));

    public static readonly DirectProperty<BlurredCoverBackdrop, double> EffectiveDimProperty =
        AvaloniaProperty.RegisterDirect<BlurredCoverBackdrop, double>(nameof(EffectiveDim), o => o.EffectiveDim);

    /// <summary>The sharp cover's bitmap (the page's CachedImage), the one to blur.</summary>
    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>The blur, in percent of PreBlurredArtworkConverter's stock sigma.</summary>
    public int BlurPercent { get => GetValue(BlurPercentProperty); set => SetValue(BlurPercentProperty, value); }

    /// <summary>Blurred Cover is the chosen style; off, nothing is blurred and the bitmaps go.</summary>
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    private bool _hasImage;

    /// <summary>A blurred cover is showing: the page hides its wash under it.</summary>
    public bool HasImage
    {
        get => _hasImage;
        private set => SetAndRaise(HasImageProperty, ref _hasImage, value);
    }

    /// <summary>The user's Dim (Settings), 0..1: the black laid over the blurred cover.</summary>
    public double Dim { get => GetValue(DimProperty); set => SetValue(DimProperty, value); }

    private double _contrastDim;
    private double _effectiveDim;

    /// <summary>The Dim to draw: the user's, or more where a light cover needs it for white text
    /// (<see cref="ContrastDim"/>). The user's value stays the floor, so the slider still darkens.</summary>
    public double EffectiveDim
    {
        get => _effectiveDim;
        private set => SetAndRaise(EffectiveDimProperty, ref _effectiveDim, value);
    }

    /// <summary>The least Dim the showing blurred cover needs (<see cref="ContrastDimFor"/>); 0 without one.</summary>
    internal double ContrastDim => _contrastDim;

    /// <summary>The showing blurred cover's brightest pixel (tests).</summary>
    internal Color Brightest { get; private set; }

    // Now Playing's stock shade at its transport row (NowPlayingShade's 0.72 stop: about 72% down,
    // where MobileNowPlayingPageTests holds the wash to white text 4.5:1 over a white cover), drawn at
    // BlurShade's 40% over the blurred cover.
    internal const double ShadeAtTransport = 0xA6 / 255.0, ShadeOpacity = 0.4;

    /// <summary>White text over the transport row keeps the wash's 4.5:1 (WCAG AA).</summary>
    internal const double MinTextContrast = 4.5;

    /// <summary>
    /// Discord #mobile-beta (11-19): on a light cover the default Dim (15%) left white text weak, as
    /// low as 2.6:1 under the transport on a white cover where the wash gives 4.5:1. The least Dim, in
    /// whole percent, that brings the cover's brightest spot under the stock shade down far enough
    /// for white text to keep <see cref="MinTextContrast"/> there; 0 for a cover that needs none.
    /// Higher up the shade is lighter, but the title's large bold type needs only 3:1 (it gets 3.3:1
    /// or more at the top of a landscape screen).
    /// </summary>
    internal static double ContrastDimFor(Color brightest)
    {
        for (var percent = 0; percent < 100; percent++)
        {
            var keep = (1 - ShadeAtTransport * ShadeOpacity) * (1 - percent / 100.0);
            var under = Color.FromRgb(Scale(brightest.R, keep), Scale(brightest.G, keep), Scale(brightest.B, keep));
            if (Services.MobileTheme.ContrastRatio(Colors.White, under) >= MinTextContrast) return percent / 100.0;
        }
        return 1;

        static byte Scale(byte channel, double keep) => (byte)Math.Round(channel * keep);
    }

    internal static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);

    private Bitmap? _current;
    private Bitmap? _previous;
    private readonly Stopwatch _fade = new();
    private bool _fading;
    private int _generation;

    /// <summary>The bitmap drawn (tests).</summary>
    internal Bitmap? Current => _current;

    /// <summary>The last blur started (tests await it, then run the UI jobs).</summary>
    internal Task PendingBuild { get; private set; } = Task.CompletedTask;

    /// <summary>A cross-fade is running (tests).</summary>
    internal bool IsFading => _fading;

    static BlurredCoverBackdrop()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<BlurredCoverBackdrop>(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty) Rebuild(fade: true);
        else if (change.Property == BlurPercentProperty || change.Property == IsActiveProperty) Rebuild(fade: false);
        else if (change.Property == DimProperty) EffectiveDim = Math.Max(Dim, _contrastDim);
    }

    private async void Rebuild(bool fade)
    {
        var generation = ++_generation;
        if (!IsActive || Source is not Bitmap source)
        {
            Show(null, default, fade: false);
            return;
        }
        var percent = Math.Max(0, BlurPercent);
        // The cover stays alive while the pool reads it: CachedImage lets go of it on the next song.
        ArtworkCache.Acquire(source);
        Bitmap? blurred = null;
        Color brightest = default;
        try
        {
            var build = Task.Run(() => (PreBlurredArtworkConverter.BlurredCopy(source, percent, out var b), b));
            PendingBuild = build;
            (blurred, brightest) = await build;
        }
        catch (Exception ex)
        {
            // No blurred cover beats a crash: the page keeps its wash.
            DebugLog.Write("NowPlaying", $"Blurred cover failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ArtworkCache.Release(source);
        }
        if (generation != _generation)
        {
            blurred?.Dispose();   // a newer cover or Blur step came in meanwhile
            return;
        }
        Show(blurred, brightest, fade);
    }

    private void Show(Bitmap? next, Color brightest, bool fade)
    {
        var old = _current;
        _current = next;
        Brightest = next != null ? brightest : default;
        _contrastDim = next != null ? ContrastDimFor(brightest) : 0;
        EffectiveDim = Math.Max(Dim, _contrastDim);
        // A fade's outgoing bitmap is done with either way.
        if (_previous != null && !ReferenceEquals(_previous, old)) Release(_previous);
        _previous = null;
        if (old != null)
        {
            if (fade && next != null && CanFade())
            {
                _previous = old;
                StartFade();
            }
            else Release(old);
        }
        if (_previous == null) _fading = false;
        HasImage = _current != null;
        InvalidateVisual();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A new blurred bitmap (or none) is showing: the Lyrics page's soft edges repaint it.</summary>
    public event EventHandler? ImageChanged;

    /// <summary>Battery Saver keeps the swap still, as it keeps the Background's Slow Motion still.</summary>
    private bool CanFade() => DataContext is not ShellViewModel { BatterySaver.IsOn: true };

    // Disposed after the frame that last drew it has gone out (the converter's pattern).
    private static void Release(Bitmap bitmap) => Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);

    private void StartFade()
    {
        _fade.Restart();
        if (_fading) return;
        _fading = true;
        Request();
    }

    private void Request()
    {
        if (TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(_ => Step());
        else EndFade();
    }

    private void Step()
    {
        if (!_fading) return;
        if (_fade.Elapsed >= FadeDuration) EndFade();
        else Request();
        InvalidateVisual();
    }

    /// <summary>The fade's end: the old cover goes (internal: the tests end it without frames).</summary>
    internal void EndFade()
    {
        _fading = false;
        if (_previous != null) Release(_previous);
        _previous = null;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_fading) EndFade();
    }

    public override void Render(DrawingContext context)
    {
        if (_current == null) return;
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (_fading && _previous != null)
        {
            Draw(context, _previous, bounds);
            var t = Math.Clamp(_fade.Elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds, 0, 1);
            using (context.PushOpacity(t * t * (3 - 2 * t)))
                Draw(context, _current, bounds);
        }
        else Draw(context, _current, bounds);
    }

    /// <summary>The bitmap filling the bounds, cropped to their shape (UniformToFill): the cover
    /// keeps its proportions, a square on a tall screen loses its sides.</summary>
    private static void Draw(DrawingContext context, Bitmap bitmap, Rect bounds)
    {
        var size = bitmap.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var scale = Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
        var crop = new Size(bounds.Width / scale, bounds.Height / scale);
        var source = new Rect((size.Width - crop.Width) / 2, (size.Height - crop.Height) / 2, crop.Width, crop.Height);
        context.DrawImage(bitmap, source, bounds);
    }
}

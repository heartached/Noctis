using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// In-window version of the <see cref="PillDialogHost"/> backdrop (owner 10-08: "inside of
/// the settings, make the background blur just like the metadata pop up"): a blurred copy of
/// <see cref="Target"/> under a dim, for a sheet that lives inside the window rather than in
/// a pop-up window of its own.
///
/// The snapshot is taken once per open (<see cref="PrepareAsync"/>) and freed on close
/// (<see cref="Release"/>); while it is up the layer is a plain bitmap draw. Closed (not
/// visible, no snapshot) it costs nothing.
///
/// <see cref="Target"/> must be a sibling that covers the same area as this layer and holds
/// everything the sheet sits over, but not this layer or the sheet: the snapshot renders
/// that subtree only, so it can be retaken while the sheet is up (a resize, a theme change
/// made from the sheet) without catching the sheet or the dim in it. The image fills this
/// layer, so a resize stretches the old snapshot until the retake lands.
///
/// Without a snapshot (not taken yet, or the backend can't) the layer is the plain
/// <see cref="Dim"/>; with one, the dim drops to <see cref="BlurDimOpacity"/> of it, the
/// pop-up's lighter dim, since the blur does most of the separating.
/// </summary>
public class BlurredBackdrop : Panel
{
    /// <summary>How long <see cref="PrepareAsync"/> waits for the snapshot before the caller
    /// shows the sheet anyway (PillDialogHost's budget). It normally lands within a frame or
    /// two; a late one fades in where it is.</summary>
    public static readonly TimeSpan SnapshotWaitBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>Quiet time after the last resize / <see cref="ScheduleRefresh"/> before the
    /// snapshot is retaken, so a window drag retakes it once, not per step.</summary>
    internal static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Snapshot fade-in / dim change when the snapshot lands while the layer is
    /// already showing (the Settings reopen from Statistics shows the dim at once).</summary>
    private static readonly TimeSpan CrossfadeDuration = TimeSpan.FromMilliseconds(140);

    public static readonly StyledProperty<IBrush?> DimProperty =
        AvaloniaProperty.Register<BlurredBackdrop, IBrush?>(nameof(Dim), new ImmutableSolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)));

    public static readonly StyledProperty<double> BlurDimOpacityProperty =
        AvaloniaProperty.Register<BlurredBackdrop, double>(nameof(BlurDimOpacity), 0x50 / (double)0xA6);

    /// <summary>The dim over the app. Alone (no snapshot) it is the whole backdrop.</summary>
    public IBrush? Dim
    {
        get => GetValue(DimProperty);
        set => SetValue(DimProperty, value);
    }

    /// <summary>How much of <see cref="Dim"/> stays over the blurred snapshot. The default
    /// turns the plain #A6 black into the pop-up's #50 (PillDialogScrimBrush).</summary>
    public double BlurDimOpacity
    {
        get => GetValue(BlurDimOpacityProperty);
        set => SetValue(BlurDimOpacityProperty, value);
    }

    /// <summary>What the snapshot renders: see the class remarks.</summary>
    public Visual? Target { get; set; }

    private readonly Image _image;
    private readonly Border _dim;
    private readonly Transitions _imageFade;
    private readonly Transitions _dimFade;
    private WriteableBitmap? _snapshot;
    /// <summary>The unblurred snapshot behind <see cref="_snapshot"/>, kept while it is up so
    /// the Background Blur slider re-blurs it live instead of taking a new snapshot per step.</summary>
    private BackdropSnapshot.Source? _source;
    /// <summary>Re-blur output buffer (one re-blur runs at a time).</summary>
    private byte[]? _reblurBuffer;
    private bool _reblurRunning;
    private bool _reblurAgain;
    /// <summary>The slider is at Off while a snapshot is up: the image is faded out under the
    /// plain dim, and kept so moving the slider back fades it in without a new snapshot.</summary>
    private bool _blurHidden;
    private Task<bool>? _capture;
    /// <summary>Bumped by every <see cref="ScheduleRefresh"/> (and <see cref="Release"/>):
    /// only the wait that is still the latest retakes.</summary>
    private int _refreshStamp;
    private bool _refreshPending;
    /// <summary>Bumped by <see cref="Release"/>: a capture that started before it lands stale
    /// and is thrown away instead of shown.</summary>
    private int _epoch;

    public BlurredBackdrop()
    {
        IsHitTestVisible = false;
        _image = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0,
        };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.MediumQuality);
        _dim = new Border();
        _dim.Bind(Border.BackgroundProperty, this.GetObservable(DimProperty));
        Children.Add(_image);
        Children.Add(_dim);
        // A light/dark switch repaints the whole app under the sheet.
        ActualThemeVariantChanged += (_, _) => ScheduleRefresh();

        _imageFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
        _dimFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
    }

    /// <summary>The blurred snapshot currently shown, if any.</summary>
    public bool HasSnapshot => _snapshot != null;
    internal Bitmap? Snapshot => _snapshot;
    internal Image SnapshotImage => _image;
    internal Border DimLayer => _dim;
    /// <summary>True while a retake is scheduled (tests).</summary>
    internal bool IsRefreshPending => _refreshPending;
    /// <summary>The radius the shown snapshot was blurred at, and how many live re-blurs ran (tests).</summary>
    internal double ShownRadius { get; private set; }
    internal int ReblurCount { get; private set; }

    /// <summary>
    /// Call when the sheet starts to open: takes the snapshot unless one is still up (a
    /// reopen during the close fade keeps it), and returns once it is shown or
    /// <see cref="SnapshotWaitBudget"/> has passed, whichever is first. Never throws.
    /// </summary>
    public async Task PrepareAsync()
    {
        if (_snapshot != null) return;
        var capture = CaptureAsync();
        await Task.WhenAny(capture, Task.Delay(SnapshotWaitBudget));
    }

    /// <summary>Takes (or retakes) the snapshot and shows it. True when one is shown. A
    /// capture already running is joined rather than doubled.</summary>
    internal Task<bool> CaptureAsync() =>
        _capture is { IsCompleted: false } running ? running : _capture = CaptureCoreAsync();

    private async Task<bool> CaptureCoreAsync()
    {
        var epoch = _epoch;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        WriteableBitmap? bitmap = null;
        BackdropSnapshot.Source? source = null;
        var radius = BackdropSnapshot.BlurRadius;
        try
        {
            // Background Blur off: no snapshot, the plain dim alone.
            if (BackdropSnapshot.IsBlurEnabled && Target is { } target && TopLevel.GetTopLevel(this) is { } top)
            {
                source = await BackdropSnapshot.CaptureSourceAsync(target, top, "BlurredBackdrop");
                if (source != null && epoch == _epoch)
                {
                    radius = BackdropSnapshot.BlurRadius;
                    var pixels = new byte[source.Width * source.Height * 4];
                    await Task.Run(() => source.BlurInto(pixels, radius));
                    bitmap = source.CreateBitmap(pixels);
                }
            }
        }
        finally
        {
            if (epoch == _epoch) _capture = null;
        }
        if (bitmap is null) return false;
        if (epoch != _epoch)
        {
            // Closed (Release) while it was being taken.
            bitmap.Dispose();
            return false;
        }

        DebugLogger.Info(DebugLogger.Category.UI, "BlurredBackdrop.Capture",
            $"{bitmap.PixelSize.Width}x{bitmap.PixelSize.Height} in {timer.ElapsedMilliseconds} ms{(_snapshot is null ? "" : " (retake)")}");
        var first = _snapshot is null;
        var old = _snapshot;
        _snapshot = bitmap;
        _source = source;
        ShownRadius = radius;
        _image.Source = bitmap;
        old?.Dispose();
        if (first || _blurHidden)
        {
            // Showing already (the dim went up before the snapshot): cross-fade from the
            // sharp app under the full dim to the blur under the lighter one. Not showing
            // yet: settle at once, the layer's own fade carries it in.
            var showing = IsEffectivelyVisible && Opacity > 0;
            ShowBlur(animate: showing);
        }
        // The slider moved while this was being taken: catch up on the new source.
        if (ShownRadius != BackdropSnapshot.BlurRadius) OnBlurRadiusChanged(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The blurred snapshot under the lighter dim, or (slider at Off) faded out under
    /// the plain dim with the snapshot kept.</summary>
    private void ShowBlur(bool animate)
    {
        _blurHidden = !BackdropSnapshot.IsBlurEnabled;
        if (_blurHidden)
            SetLayers(imageOpacity: 0, dimOpacity: 1, animate: animate);
        else
            SetLayers(imageOpacity: 1, dimOpacity: Math.Clamp(BlurDimOpacity, 0, 1), animate: animate);
    }

    /// <summary>
    /// Re-blurs the kept source at the slider's current radius, off the UI thread, into the
    /// bitmap already shown. One runs at a time; changes that land meanwhile are folded into
    /// one more pass at the newest value, so a drag follows the thumb without queueing up.
    /// </summary>
    private async Task ReblurAsync()
    {
        if (_reblurRunning) { _reblurAgain = true; return; }
        _reblurRunning = true;
        try
        {
            do
            {
                _reblurAgain = false;
                var epoch = _epoch;
                var source = _source;
                var bitmap = _snapshot;
                if (source is null || bitmap is null) return;
                var radius = BackdropSnapshot.BlurRadius;
                if (radius <= 0 || radius == ShownRadius) continue;
                var length = source.Width * source.Height * 4;
                if (_reblurBuffer?.Length != length) _reblurBuffer = new byte[length];
                var buffer = _reblurBuffer;
                await Task.Run(() => source.BlurInto(buffer, radius));
                if (epoch != _epoch) return;              // closed meanwhile
                if (!ReferenceEquals(bitmap, _snapshot))  // retaken meanwhile: redo on the new one
                {
                    _reblurAgain = true;
                    continue;
                }
                source.CopyInto(bitmap, buffer);
                _image.InvalidateVisual();
                ShownRadius = radius;
                ReblurCount++;
            }
            while (_reblurAgain);
        }
        finally
        {
            _reblurRunning = false;
        }
    }

    /// <summary>
    /// Call once the sheet has closed (the layer is hidden): frees the snapshot and puts the
    /// layer back to the plain dim. A capture still running is thrown away when it lands.
    /// </summary>
    public void Release()
    {
        _epoch++;
        _capture = null;
        _refreshStamp++;
        _refreshPending = false;
        _image.Source = null;
        _snapshot?.Dispose();
        _snapshot = null;
        _source = null;
        _reblurBuffer = null;
        _blurHidden = false;
        SetLayers(imageOpacity: 0, dimOpacity: 1, animate: false);
    }

    /// <summary>Retakes the snapshot after <see cref="RefreshDelay"/> of quiet, if one is up
    /// (something the sheet changed — the theme, Liquid Glass — repainted the app under it).
    /// Restarting the wait on every call coalesces a burst into one retake.</summary>
    public void ScheduleRefresh()
    {
        if (_snapshot is null) return;
        _refreshPending = true;
        _ = RefreshAfterQuietAsync(++_refreshStamp);
    }

    /// <summary>Task.Delay (resumed on the UI thread) as PillDialogHost's deferred close,
    /// rather than a DispatcherTimer.</summary>
    private async Task RefreshAfterQuietAsync(int stamp)
    {
        await Task.Delay(RefreshDelay);
        if (stamp != _refreshStamp) return;
        _refreshPending = false;
        if (_snapshot != null) await CaptureAsync();
    }

    /// <summary>
    /// Settings → Background Blur moved while the sheet is up (the slider lives on it): the
    /// kept source is re-blurred live at the new strength (<see cref="ReblurAsync"/>), Off
    /// cross-fades to the plain dim and back, and with no snapshot up yet (the sheet opened
    /// with blur off) one is taken at once and fades in.
    /// </summary>
    private void OnBlurRadiusChanged(object? sender, EventArgs e)
    {
        if (_snapshot != null)
        {
            var hidden = !BackdropSnapshot.IsBlurEnabled;
            if (hidden != _blurHidden) ShowBlur(animate: IsEffectivelyVisible && Opacity > 0);
            if (!hidden) _ = ReblurAsync();
            return;
        }
        if (BackdropSnapshot.IsBlurEnabled && IsEffectivelyVisible && Opacity > 0)
            _ = CaptureAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BackdropSnapshot.BlurRadiusChanged += OnBlurRadiusChanged;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // The image fills the layer, so the old snapshot stretches to the new size at once;
        // the retake replaces it once the resize settles.
        ScheduleRefresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        BackdropSnapshot.BlurRadiusChanged -= OnBlurRadiusChanged;
        Release();
    }

    private void SetLayers(double imageOpacity, double dimOpacity, bool animate)
    {
        _image.Transitions = animate ? _imageFade : null;
        _dim.Transitions = animate ? _dimFade : null;
        _image.Opacity = imageOpacity;
        _dim.Opacity = dimOpacity;
    }
}

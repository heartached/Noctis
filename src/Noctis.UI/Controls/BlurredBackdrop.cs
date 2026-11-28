using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Services;
using SkiaSharp;

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
///
/// <see cref="LiveBlur"/> swaps the snapshot for a live blur of <see cref="Target"/> itself.
/// </summary>
public class BlurredBackdrop : Panel
{
    /// <summary>How long <see cref="PrepareAsync"/> waits for the snapshot before the caller
    /// shows the sheet anyway (PillDialogHost's budget). It normally lands within a frame or
    /// two; a late one fades in where it is.</summary>
    public static readonly TimeSpan SnapshotWaitBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>Quiet time after the last resize / <see cref="ScheduleRefresh"/> before the
    /// snapshot is retaken, so a window drag retakes it once, not per step. A setting changed
    /// from the sheet doesn't wait for it (<see cref="RefreshSoon"/>).</summary>
    internal static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Snapshot fade-in / dim change when the snapshot lands while the layer is
    /// already showing (the Settings reopen from Statistics shows the dim at once).</summary>
    private static readonly TimeSpan CrossfadeDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>Liquid Glass switched off from the open sheet: the glass-look snapshot fades out
    /// over the live blur of the new look (<see cref="OnAppGlassChanged"/>).</summary>
    internal static readonly TimeSpan ModeSwitchDuration = TimeSpan.FromMilliseconds(220);

    public static readonly StyledProperty<IBrush?> DimProperty =
        AvaloniaProperty.Register<BlurredBackdrop, IBrush?>(nameof(Dim), new ImmutableSolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)));

    public static readonly StyledProperty<double> BlurDimOpacityProperty =
        AvaloniaProperty.Register<BlurredBackdrop, double>(nameof(BlurDimOpacity), 0x50 / (double)0xA6);

    public static readonly DirectProperty<BlurredBackdrop, bool> IsOpaqueCoverProperty =
        AvaloniaProperty.RegisterDirect<BlurredBackdrop, bool>(nameof(IsOpaqueCover), o => o.IsOpaqueCover);

    private bool _isOpaqueCover;

    /// <summary>
    /// True while the layer hides <see cref="Target"/> completely: the blurred snapshot (opaque
    /// everywhere, see BackdropSnapshot.FlattenOpaque) is shown at full strength, with the layer
    /// itself at full opacity. Nothing under the layer can then change a pixel, and what does
    /// change the layer (a retake's cross-fade, the dim, the open/close fade) repaints all of it.
    /// The Settings sheet's frost reads this (GlassPanel.BeneathRepaintsWhole).
    /// </summary>
    public bool IsOpaqueCover
    {
        get => _isOpaqueCover;
        private set => SetAndRaise(IsOpaqueCoverProperty, ref _isOpaqueCover, value);
    }

    private void UpdateOpaqueCover()
    {
        // A retake's cross-fade covers it too: the new snapshot fades in over the old one at full
        // strength, both opaque (11-27: the app under the sheet came back for the whole fade, the
        // newly themed page drawn behind two opaque bitmaps, and the sheet's frost re-read it).
        var shown = ReferenceEquals(_image.Source, _snapshot)
                    && (_image.Opacity >= 1 || _fadingOut.Source is not null && _fadingOut.Opacity >= 1);
        IsOpaqueCover = _snapshot != null && !_live && !_blurHidden && shown && Opacity >= 1 && IsVisible;
        ApplyHostHidden();
    }

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

    /// <summary>
    /// Live mode (owner 10-18: under Settings the lyrics "skip/jump"): instead of a frozen
    /// snapshot, <see cref="Target"/> itself is blurred in place (a BlurEffect) while the
    /// layer is up, so what moves under the sheet (synced lyrics, the animated background)
    /// keeps moving, blurred, instead of standing still and jumping forward on every retake.
    /// No snapshot, no retakes: a theme switch or a resize shows under it at once. The blur
    /// follows this layer's Opacity, so it fades in and out with the dim. Falls back to the
    /// snapshot while Liquid Glass is on (<see cref="AppGlass"/>): a GlassPanel frosts the
    /// surface it draws on, which inside an effect layer is not what lies beneath it.
    /// </summary>
    public bool LiveBlur { get; set; }

    /// <summary>Live mode is on (the effect is, or at Opacity 0 will be, on Target).</summary>
    private bool _live;
    private IEffect? _liveEffect;

    private readonly Image _image;
    /// <summary>The snapshot a retake replaced, held under the new one while it fades in.</summary>
    private readonly Image _fadingOut;
    private Bitmap? _fadingBitmap;
    private readonly Border _dim;
    private readonly Transitions _imageFade;
    private readonly Transitions _dimFade;
    private readonly Transitions _modeSwitchFade;
    /// <summary>The glass-look snapshot is fading out over the live blur (glass switched off
    /// from the sheet); the stand-in waits for it, as it would cover the fade.</summary>
    private bool _handingOff;
    /// <summary>Bumped by every switch to glass while live: only the latest hand-over completes.</summary>
    private int _handOverStamp;
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
    /// <summary>A <see cref="RefreshSoon"/> retake is waiting for its Background hop.</summary>
    private bool _soonQueued;
    /// <summary>When <see cref="RefreshSoon"/> was last called (Stopwatch timestamp, 0 = never).</summary>
    private long _lastSoonRequest;
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
        _fadingOut = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        RenderOptions.SetBitmapInterpolationMode(_fadingOut, BitmapInterpolationMode.MediumQuality);
        _dim = new Border();
        _dim.Bind(Border.BackgroundProperty, this.GetObservable(DimProperty));
        _probe = new ChangeProbe(RequestRetake);
        _bands = new BandFrame[4];
        _standIn = new Panel { IsVisible = false, IsHitTestVisible = false };
        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i] = new BandFrame
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsVisible = false,
                Probe = _probe,
                UseLayoutRounding = false,
            };
            // Stretched ~3-8x from a heavily blurred render: bicubic keeps the gradients smooth.
            RenderOptions.SetBitmapInterpolationMode(_bands[i], BitmapInterpolationMode.HighQuality);
            _standIn.Children.Add(_bands[i]);
        }
        _blurSource = new BandFrame { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        _blurrer = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = _blurSource };
        _blurHost = new Panel
        {
            Width = 0, Height = 0, ClipToBounds = true, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Children = { _blurrer },
        };
        Children.Add(_blurHost);
        Children.Add(_fadingOut);
        Children.Add(_image);
        Children.Add(_standIn);
        Children.Add(_probe);
        Children.Add(_dim);
        _image.PropertyChanged += (_, e) =>
        {
            if (e.Property == OpacityProperty || e.Property == Image.SourceProperty) UpdateOpaqueCover();
        };
        _fadingOut.PropertyChanged += (_, e) =>
        {
            if (e.Property == OpacityProperty || e.Property == Image.SourceProperty) UpdateOpaqueCover();
        };
        // A light/dark switch repaints the whole app under the sheet.
        ActualThemeVariantChanged += (_, _) => RefreshSoon();

        _imageFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
        _dimFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
        _modeSwitchFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = ModeSwitchDuration, Easing = new CubicEaseOut() } };
    }

    /// <summary>The blurred snapshot currently shown, if any.</summary>
    public bool HasSnapshot => _snapshot != null;
    internal Bitmap? Snapshot => _snapshot;
    internal Image SnapshotImage => _image;
    internal Image FadingImage => _fadingOut;
    internal Border DimLayer => _dim;
    /// <summary>True while a retake is scheduled (tests).</summary>
    internal bool IsRefreshPending => _refreshPending;
    /// <summary>The radius the shown snapshot was blurred at, and how many live re-blurs ran (tests).</summary>
    internal double ShownRadius { get; private set; }
    internal int ReblurCount { get; private set; }
    /// <summary>A snapshot is being taken (tests).</summary>
    internal bool IsCapturing => _capture is { IsCompleted: false };
    /// <summary>How many <see cref="RefreshSoon"/> retakes waited for a rendered frame first (tests).</summary>
    internal int RetakesAfterFrame { get; private set; }
    /// <summary>How many snapshots were taken and shown (tests).</summary>
    internal int CaptureCount { get; private set; }
    /// <summary>Live mode is on, and the BlurEffect radius currently on Target (tests).</summary>
    internal bool IsLive => _live;
    internal double LiveEffectRadius { get; private set; }

    private bool WantsLive => LiveBlur && !AppGlass.IsActive && Target != null;

    /// <summary>
    /// The BlurEffect radius that blurs as much as the snapshot does at the same Background
    /// Blur setting: the snapshot's three box passes of radius r at 1/4 scale make a gaussian
    /// of sigma ~ 4 * sqrt(r(r+1)) logical px, and Skia turns a BlurEffect radius R into sigma
    /// 0.288675 * R + 0.5 (both measured: BlurredBackdropLiveTests.LiveRadius_MatchesTheSnapshotsBlur).
    /// The default 1 gives ~18, the slider's 10 gives ~144.
    /// </summary>
    internal static double LiveRadiusFor(double blurRadius)
    {
        if (!(blurRadius > 0)) return 0;
        var sigma = Math.Sqrt(blurRadius * (blurRadius + 1)) / BackdropSnapshot.SnapshotScaling;
        return Math.Max(0, (sigma - 0.5) / 0.288675);
    }

    /// <summary>
    /// Call when the sheet starts to open: takes the snapshot unless one is still up (a
    /// reopen during the close fade keeps it), and returns once it is shown or
    /// <see cref="SnapshotWaitBudget"/> has passed, whichever is first. Never throws.
    /// </summary>
    public async Task PrepareAsync()
    {
        if (WantsLive)
        {
            StartLive();
            // One frame at Opacity 0 first, as a snapshot takes one: the fade then animates.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
            return;
        }
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
        CaptureCount++;
        if (old != null && !_blurHidden && IsEffectivelyVisible && Opacity > 0)
            CrossfadeFrom(old);
        else
        {
            _image.Source = bitmap;
            old?.Dispose();
        }
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

    /// <summary>A retake while the sheet is up: the new snapshot fades in over the old one
    /// (both opaque, so the mix never shows the app or the dim through), which is then freed.
    /// A retake landing mid-fade drops the older one at once.</summary>
    private void CrossfadeFrom(Bitmap old)
    {
        _fadingOut.Source = old;
        _fadingBitmap?.Dispose();
        _fadingBitmap = old;
        _image.Transitions = null;
        _image.Opacity = 0;
        _image.Source = _snapshot;
        _image.Transitions = _imageFade;
        _image.Opacity = 1;
        _ = FreeFadedAsync(old);
    }

    private async Task FreeFadedAsync(Bitmap old)
    {
        await Task.Delay(CrossfadeDuration + TimeSpan.FromMilliseconds(60));
        if (!ReferenceEquals(_fadingBitmap, old)) return; // replaced or released meanwhile
        _fadingOut.Source = null;
        _fadingBitmap = null;
        old.Dispose();
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
        StopLive();
        _epoch++;
        _capture = null;
        _refreshStamp++;
        _refreshPending = false;
        _image.Source = null;
        _snapshot?.Dispose();
        _snapshot = null;
        _fadingOut.Source = null;
        _fadingBitmap?.Dispose();
        _fadingBitmap = null;
        _source = null;
        _reblurBuffer = null;
        _blurHidden = false;
        _handingOff = false;
        _handOverStamp++;
        SetLayers(imageOpacity: 0, dimOpacity: 1, animate: false);
    }

    /// <summary>Retakes the snapshot after <see cref="RefreshDelay"/> of quiet, if one is up
    /// (something the sheet changed — the theme, Liquid Glass — repainted the app under it).
    /// Restarting the wait on every call coalesces a burst into one retake.</summary>
    public void ScheduleRefresh()
    {
        if (_snapshot is null) return;
        _refreshPending = true;
        _ = RefreshAfterQuietAsync(++_refreshStamp, soon: false);
    }

    /// <summary>Retakes the snapshot once the current change has rendered, without
    /// <see cref="RefreshDelay"/> (owner 10-09: a theme or Liquid Glass switch from the sheet
    /// left the app under it on the old look for a beat). The calls of one burst (every flag
    /// a theme switch raises) make one retake, which cross-fades in.</summary>
    public void RefreshSoon()
    {
        if (_snapshot is null) return;
        _refreshPending = true;
        // The rest of this burst: the queued hop retakes after it anyway.
        if (_soonQueued) return;
        // Owner 10-18: a slider dragged on the sheet raises a change per pointer move, and
        // each one retook the whole app back to back (30-57 captures for a one-second drag),
        // so the thumb stuttered. A request within RefreshDelay of the previous one is such a
        // stream: it waits for the stream to go quiet and retakes once.
        var streaming = _lastSoonRequest != 0 && Stopwatch.GetElapsedTime(_lastSoonRequest) < RefreshDelay;
        _lastSoonRequest = Stopwatch.GetTimestamp();
        _soonQueued = !streaming;
        _ = RefreshAfterQuietAsync(++_refreshStamp, soon: !streaming);
    }

    /// <summary>Task.Delay (resumed on the UI thread) as PillDialogHost's deferred close,
    /// rather than a DispatcherTimer. Soon: a Background-priority hop instead, which runs
    /// after the rest of the current job and the render it queued (the theme is applied after
    /// the flags that ask for the retake), so the snapshot is of the new look.</summary>
    private async Task RefreshAfterQuietAsync(int stamp, bool soon)
    {
        if (soon)
        {
            await AfterNextFrameAsync();
            _soonQueued = false;
        }
        else
            await Task.Delay(RefreshDelay);
        if (stamp != _refreshStamp) return;
        // A capture already running started before this change: let it land, then retake.
        if (_capture is { IsCompleted: false } running)
        {
            await running;
            if (stamp != _refreshStamp) return;
        }
        _refreshPending = false;
        if (_snapshot != null) await CaptureAsync();
    }

    /// <summary>
    /// Resumes once the next frame has been rendered (11-27: a theme switch from the open sheet
    /// queued its retake behind a Background hop only, which can run before the render tick, so
    /// the switch's first frame waited for the capture as well). The retake now starts after the
    /// frame that shows the new look under the old snapshot; not drawing (minimised), it waits
    /// <see cref="FrameWaitLimit"/> at most.
    /// </summary>
    private async Task AfterNextFrameAsync()
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            var rendered = new TaskCompletionSource();
            // The callback runs as the frame starts; a Background job posted from it runs once
            // that frame's layout and render are done.
            top.RequestAnimationFrame(_ => Dispatcher.UIThread.Post(() => rendered.TrySetResult(), DispatcherPriority.Background));
            await Task.WhenAny(rendered.Task, Task.Delay(FrameWaitLimit));
            RetakesAfterFrame++;
        }
        else
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    /// <summary>Longest wait for a frame before a <see cref="RefreshSoon"/> retake goes ahead anyway.</summary>
    internal static readonly TimeSpan FrameWaitLimit = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Settings → Background Blur moved while the sheet is up (the slider lives on it): the
    /// kept source is re-blurred live at the new strength (<see cref="ReblurAsync"/>), Off
    /// cross-fades to the plain dim and back, and with no snapshot up yet (the sheet opened
    /// with blur off) one is taken at once and fades in.
    /// </summary>
    private void OnBlurRadiusChanged(object? sender, EventArgs e)
    {
        if (_live)
        {
            // Live: the new strength is just a new radius on the effect, Off the plain dim.
            if (!BackdropSnapshot.IsBlurEnabled != _blurHidden) SetLiveLayers(animate: IsEffectivelyVisible && Opacity > 0);
            UpdateLiveEffect();
            return;
        }
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
        AppGlass.Changed += OnAppGlassChanged;
        LayoutUpdated += OnLayoutUpdatedForStandIn;
    }

    private Size _standInTargetSize;

    /// <summary>After any layout pass: the bands follow the sheet (a plugin view growing the card)
    /// and the window; a new size of the app also needs a new frame.</summary>
    private void OnLayoutUpdatedForStandIn(object? sender, EventArgs e)
    {
        if (!_standingIn || Target is not { } target) return;
        LayoutBands();
        if (target.Bounds.Size != _standInTargetSize)
        {
            _standInTargetSize = target.Bounds.Size;
            RequestRetake();
        }
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
        AppGlass.Changed -= OnAppGlassChanged;
        LayoutUpdated -= OnLayoutUpdatedForStandIn;
        Release();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The open/close fade of this layer carries the live blur with it (the stand-in gives
        // way to it at the fade's first step, and takes over once the layer is fully up).
        if (_live && change.Property == OpacityProperty) UpdateLiveEffect();
        if (change.Property == IsVisibleProperty) UpdateStandIn();
        if (change.Property == OpacityProperty || change.Property == IsVisibleProperty) UpdateOpaqueCover();
    }

    /// <summary>Puts live mode on (idempotent): the dim settles at the blur's lighter one
    /// (the plain dim at Off) and Target gets its BlurEffect for the current Opacity.</summary>
    private void StartLive()
    {
        _live = true;
        // Target fills the window, so clipping it to its bounds changes nothing on screen; but a
        // render of it on its own (the stand-in's) culls what lies outside it the way the window
        // does. Without the clip, content past the window's edge (a drawer parked off-screen, a
        // row below the fold) was blurred into the stand-in's edges.
        if (Target is { } target && !_clippedTarget)
        {
            _targetClipWasSet = target.IsSet(ClipToBoundsProperty);
            _targetClipWas = target.ClipToBounds;
            target.ClipToBounds = true;
            _clippedTarget = true;
        }
        SetLiveLayers(animate: IsEffectivelyVisible && Opacity > 0);
        UpdateLiveEffect();
    }

    private void SetLiveLayers(bool animate)
    {
        _blurHidden = !BackdropSnapshot.IsBlurEnabled;
        SetLayers(imageOpacity: 0, dimOpacity: _blurHidden ? 1 : Math.Clamp(BlurDimOpacity, 0, 1), animate: animate);
        UpdateStandIn();
    }

    /// <summary>Target's BlurEffect at the Background Blur strength, scaled by this layer's
    /// Opacity; none at all (no offscreen layer) when that is under half a pixel.</summary>
    private void UpdateLiveEffect()
    {
        if (!_live || Target is not { } target) return;
        var radius = LiveRadiusFor(BackdropSnapshot.BlurRadius) * Math.Clamp(Opacity, 0, 1);
        if (radius < 0.5) radius = 0;
        var changed = radius != LiveEffectRadius || (radius > 0) != (_liveEffect is not null);
        if (changed)
        {
            LiveEffectRadius = radius;
            _liveEffect = radius > 0 ? new ImmutableBlurEffect(radius) : null;
        }
        UpdateStandIn();
        // Standing in: the stand-in draws the blur (a new strength is a retake); else Target does.
        if (_standingIn)
        {
            if (changed) RequestRetake();
        }
        else if (!ReferenceEquals(target.Effect, _liveEffect))
            target.Effect = _liveEffect;
    }

    /// <summary>Takes the live blur off Target (only the effect this layer put there).</summary>
    private void StopLive()
    {
        LeaveStandIn();
        if (!_live) return;
        _live = false;
        if (Target is { } target && _liveEffect != null && ReferenceEquals(target.Effect, _liveEffect))
            target.Effect = null;
        if (Target is { } clipped && _clippedTarget)
        {
            if (_targetClipWasSet) clipped.ClipToBounds = _targetClipWas;
            else clipped.ClearValue(ClipToBoundsProperty);
        }
        _clippedTarget = false;
        _liveEffect = null;
        LiveEffectRadius = 0;
    }

    // ── The stand-in: the live blur, cheaply, while the sheet sits over it ──

    /// <summary>
    /// An ancestor of <see cref="Target"/> that holds nothing else (MainWindow's AppContentHost).
    /// While a stand-in or an opaque snapshot hides Target, this goes to Opacity 0, which the
    /// compositor skips: the app under the sheet is not drawn at all. Target itself keeps
    /// Opacity 1, so it can still be rendered on its own for the stand-in's retakes and the
    /// snapshot's. Null: neither happens and the live blur stays as it always was.
    /// </summary>
    public Visual? TargetHost { get; set; }

    /// <summary>
    /// The sheet's own surface over this layer (the Settings card's fill). While it is opaque the
    /// stand-in leaves the area under it alone, so a retake never repaints the sheet's content,
    /// and changes hidden under it ask for none. Null, or not opaque: the stand-in covers it all.
    /// </summary>
    public Control? Hole { get; set; }

    /// <summary>Shortest time between two retakes of the stand-in (about 30 a second).</summary>
    internal static readonly TimeSpan RetakeInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>One more retake this long after a burst of them: a change landing in the very
    /// frame a retake showed is not seen as a change (see ChangeProbe), this catches it.</summary>
    internal static readonly TimeSpan TrailingRetakeDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The stand-in renders Target at this many device pixels per blur sigma at least,
    /// and at no less than <see cref="MinStandInScale"/> of the screen's density.</summary>
    internal const double StandInPixelsPerSigma = 4, MinStandInScale = 0.125;

    /// <summary>The bands the stand-in is drawn in: the area of this layer outside <see cref="Hole"/>.</summary>
    private readonly BandFrame[] _bands;
    private readonly Panel _standIn;
    /// <summary>Blurs a low-resolution render of Target: drawn into <see cref="_blurrer"/> with a
    /// BlurEffect and rendered on its own. Kept in a clipped 0×0 host, so it never reaches the screen
    /// and its changes repaint nothing.</summary>
    private readonly Panel _blurHost;
    private readonly Border _blurrer;
    private readonly BandFrame _blurSource;
    private readonly ChangeProbe _probe;

    private bool _clippedTarget, _targetClipWasSet, _targetClipWas;
    private bool _hostHidden, _hostOpacityWasSet;
    private double _hostOpacityWas = 1;
    private bool _standingIn;
    private Task? _entering;
    private Task? _retaking;
    private bool _retakeWanted, _trailingQueued;
    private long _lastRetake;
    private Bitmap? _standInFrame;
    /// <summary>Device pixels per layout unit of the current stand-in frame.</summary>
    private double _standInScale;
    /// <summary>Bumped whenever the stand-in ends: work that started before it is stale.</summary>
    private int _standInEpoch;
    private Rect _hole;

    /// <summary>The stand-in is up; how many times it came up and was retaken; its bands (tests).</summary>
    internal bool IsStandingIn => _standingIn;
    internal int StandInEntries { get; private set; }
    internal int RetakeCount { get; private set; }
    internal BandFrame[] Bands => _bands;
    internal bool IsTargetHostHidden => _hostHidden;

    /// <summary>
    /// Owner 11-14/15: Settings over the live blur was choppy, scrolling or not. Avalonia repaints a
    /// visual with an Effect whole whenever anything inside it changes
    /// (ServerCompositionVisual.Update: "add the entire node's old subtree bounds as a dirty
    /// region"), so with lyrics, a playing title or the progress bar moving behind the sheet the
    /// whole window, sheet included, was re-rendered and re-blurred every frame.
    ///
    /// Once the layer is fully up, a stand-in takes over: Target loses its effect and its host
    /// goes to Opacity 0; this layer draws the blur instead, from a render of Target at a fraction
    /// of the screen's resolution (blur sigma × <see cref="StandInPixelsPerSigma"/> pixels, a heavy
    /// Gaussian of it is the same picture) blurred by the same Skia blur at that resolution and
    /// stretched back. It is drawn only outside the sheet's opaque surface and retaken when
    /// something behind changes (<see cref="ChangeProbe"/>), at most every <see cref="RetakeInterval"/>,
    /// so with nothing moving it costs nothing, and scrolling the sheet never touches it. The
    /// open and close fades run on the live blur as before.
    /// </summary>
    private bool CanStandIn =>
        _live && !_blurHidden && !_handingOff && LiveEffectRadius > 0 && Opacity >= 1 && IsEffectivelyVisible
        && Target is not null && TargetHost is not null;

    private void UpdateStandIn()
    {
        if (CanStandIn)
        {
            if (!_standingIn && _entering is null) _entering = EnterStandInAsync(_standInEpoch);
        }
        else LeaveStandIn();
    }

    /// <summary>The blur sigma of the live effect in device pixels, and the device scale at Target.</summary>
    private (double Sigma, double DeviceScale)? LiveSigma()
    {
        if (Target is not { } target || TopLevel.GetTopLevel(this) is not { } top || !(LiveEffectRadius > 0)) return null;
        var scale = DeviceScale(target, top);
        return ((0.288675 * LiveEffectRadius + 0.5) * scale, scale);
    }

    /// <summary>Device pixels per layout unit for the stand-in's render of Target.</summary>
    private double StandInScale()
    {
        if (LiveSigma() is not { } s) return 0;
        var fraction = Math.Clamp(StandInPixelsPerSigma / s.Sigma, MinStandInScale, 1);
        return s.DeviceScale * fraction;
    }

    private async Task EnterStandInAsync(int epoch)
    {
        Bitmap? frame = null;
        try
        {
            if (Target is not { } target || ElementComposition.GetElementVisual(target) is not { } visual
                || target.Bounds.Width < 1 || target.Bounds.Height < 1)
                return;
            var scale = StandInScale();
            if (!(scale > 0)) return;
            // The first frame is Target rendered with its live effect, at the stand-in's scale: the
            // effect works in layout units, so it blurs exactly as much there.
            frame = await visual.Compositor.CreateCompositionVisualSnapshot(visual, scale);
            if (epoch != _standInEpoch || !CanStandIn) return;
            _standInScale = scale;
            _standInTargetSize = target.Bounds.Size;
            ShowStandInFrame(frame);
            frame = null;
            _standingIn = true;
            _probe.Arm();
            StandInEntries++;
            target.Effect = null;
            ApplyHostHidden();
        }
        catch (Exception ex)
        {
            // A backend that can't render it stays on the live blur, as before.
            DebugLogger.Warn(DebugLogger.Category.UI, "BlurredBackdrop.StandIn", ex.Message);
        }
        finally
        {
            frame?.Dispose();
            if (epoch == _standInEpoch) _entering = null;
        }
    }

    /// <summary>Ends the stand-in at once (a fade starting, the layer closing, a mode change):
    /// Target gets its host and live effect back, the bands go.</summary>
    private void LeaveStandIn()
    {
        _standInEpoch++;
        _entering = null;
        _retaking = null;
        _retakeWanted = _trailingQueued = false;
        _probe.Disarm();
        if (!_standingIn) return;
        _standingIn = false;
        ApplyHostHidden();
        UpdateLiveEffect();
        _standIn.IsVisible = false;
        foreach (var band in _bands) band.Source = null;
        _blurSource.Source = null;
        FreeLater(ref _standInFrame);
    }

    /// <summary>Asks for a retake: something behind the sheet changed, or the blur strength did.</summary>
    internal void RequestRetake()
    {
        if (!_standingIn) return;
        _retakeWanted = true;
        _retaking ??= RetakeLoopAsync(_standInEpoch);
    }

    private async Task RetakeLoopAsync(int epoch)
    {
        try
        {
            while (_retakeWanted)
            {
                _retakeWanted = false;
                var wait = _lastRetake + (long)(RetakeInterval.TotalSeconds * Stopwatch.Frequency) - Stopwatch.GetTimestamp();
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait / (double)Stopwatch.Frequency));
                if (epoch != _standInEpoch) return;
                // Start to start: a retake takes a couple of frames itself (two renders, each
                // landing after a frame), so counting from its end would halve the rate.
                _lastRetake = Stopwatch.GetTimestamp();
                await RetakeOnceAsync(epoch);
                if (epoch != _standInEpoch) return;
            }
            if (!_trailingQueued)
            {
                _trailingQueued = true;
                _ = TrailingRetakeAsync(epoch);
            }
        }
        finally
        {
            if (epoch == _standInEpoch) _retaking = null;
        }
    }

    private async Task TrailingRetakeAsync(int epoch)
    {
        await Task.Delay(TrailingRetakeDelay);
        if (epoch != _standInEpoch) return;
        if (_retaking is null) await RetakeOnceAsync(epoch);
        if (epoch == _standInEpoch) _trailingQueued = false;
    }

    /// <summary>Renders Target (no effect now) at the stand-in's scale, blurs that render through
    /// <see cref="_blurrer"/> at the same resolution, and shows the result.</summary>
    private async Task RetakeOnceAsync(int epoch)
    {
        Bitmap? raw = null, blurred = null;
        try
        {
            if (Target is not { } target || ElementComposition.GetElementVisual(target) is not { } visual) return;
            var scale = StandInScale();
            if (!(scale > 0) || LiveSigma() is not { } sigma) return;
            raw = await visual.Compositor.CreateCompositionVisualSnapshot(visual, scale);
            if (epoch != _standInEpoch) return;
            var px = raw.PixelSize;
            _blurrer.Width = px.Width;
            _blurrer.Height = px.Height;
            // The live effect's sigma, in pixels of this render (Skia: sigma = 0.288675 R + 0.5).
            var lowSigma = sigma.Sigma * scale / sigma.DeviceScale;
            _blurrer.Effect = new ImmutableBlurEffect(Math.Max(0, (lowSigma - 0.5) / 0.288675));
            _blurSource.Source = raw;
            _blurSource.SourceRect = new Rect(0, 0, px.Width, px.Height);
            _blurHost.UpdateLayout();
            if (ElementComposition.GetElementVisual(_blurrer) is not { } blurVisual) return;
            blurred = await blurVisual.Compositor.CreateCompositionVisualSnapshot(blurVisual, 1);
            if (epoch != _standInEpoch) return;
            _standInScale = scale;
            ShowStandInFrame(blurred);
            blurred = null;
            RetakeCount++;
        }
        catch (Exception ex)
        {
            DebugLogger.Warn(DebugLogger.Category.UI, "BlurredBackdrop.Retake", ex.Message);
        }
        finally
        {
            blurred?.Dispose();
            if (raw is not null)
            {
                if (ReferenceEquals(_blurSource.Source, raw)) _blurSource.Source = null;
                var r = raw;
                Dispatcher.UIThread.Post(r.Dispose, DispatcherPriority.Background);
            }
        }
    }

    /// <summary>Puts <paramref name="frame"/> (Target rendered at <see cref="_standInScale"/>) up in
    /// the bands; this layer owns it from here.</summary>
    private void ShowStandInFrame(Bitmap frame)
    {
        var old = _standInFrame;
        _standInFrame = frame;
        LayoutBands();
        _standIn.IsVisible = true;
        if (old is not null) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
    }

    /// <summary>Places the bands over this layer outside the hole, each drawing its part of the frame.</summary>
    private void LayoutBands()
    {
        var frame = _standInFrame;
        if (frame is null || Target is not { } target || target.TranslatePoint(default, this) is not { } at) return;
        // Band edges on whole device pixels, placed exactly (no layout rounding): bands rounded
        // apart left a 1 px seam where they meet (measured at 150%).
        var ds = TopLevel.GetTopLevel(this) is { } top ? DeviceScale(this, top) : 1;
        double Snap(double v) => Math.Round(v * ds) / ds;
        Rect SnapRect(Rect r) => new(new Point(Snap(r.X), Snap(r.Y)), new Point(Snap(r.Right), Snap(r.Bottom)));
        var area = new Rect(at, target.Bounds.Size);
        _hole = ComputeHole(inner: true).Intersect(area);
        if (_hole.Width > 0 && _hole.Height > 0) _hole = SnapRect(_hole);
        // Repaints within the sheet's whole bounds are its own (its list scrolling edge to edge,
        // a hover); a change of the app hidden under it does not show either.
        _probe.Hole = ComputeHole(inner: false);
        Span<Rect> rects = stackalloc Rect[4];
        var count = 0;
        if (_hole.Width <= 0 || _hole.Height <= 0)
            rects[count++] = area;
        else
        {
            void Add(Span<Rect> list, ref int n, Rect r) { if (r.Width > 0 && r.Height > 0) list[n++] = r; }
            Add(rects, ref count, new Rect(area.X, area.Y, area.Width, _hole.Y - area.Y));
            Add(rects, ref count, new Rect(area.X, _hole.Bottom, area.Width, area.Bottom - _hole.Bottom));
            Add(rects, ref count, new Rect(area.X, _hole.Y, _hole.X - area.X, _hole.Height));
            Add(rects, ref count, new Rect(_hole.Right, _hole.Y, area.Right - _hole.Right, _hole.Height));
        }
        // The render covers area × scale pixels; its pixel size is that rounded up, so mapping by
        // the pixel size would stretch it by up to a pixel and shift the far edges.
        var sx = _standInScale > 0 ? _standInScale : frame.PixelSize.Width / area.Width;
        var sy = _standInScale > 0 ? _standInScale : frame.PixelSize.Height / area.Height;
        for (var i = 0; i < _bands.Length; i++)
        {
            var band = _bands[i];
            if (i >= count)
            {
                band.IsVisible = false;
                band.Source = null;
                continue;
            }
            var r = rects[i];
            band.Margin = new Thickness(r.X, r.Y, 0, 0);
            band.Width = r.Width;
            band.Height = r.Height;
            band.SourceRect = new Rect((r.X - area.X) * sx, (r.Y - area.Y) * sy, r.Width * sx, r.Height * sy);
            band.Source = frame;
            band.IsVisible = true;
        }
    }

    /// <summary>The area under <see cref="Hole"/> the stand-in can leave out, in this layer's
    /// coordinates: the hole's bounds less its corner radius, while it paints an opaque solid fill
    /// at full strength; else empty.</summary>
    private Rect ComputeHole(bool inner)
    {
        if (Hole is not { IsEffectivelyVisible: true } hole || hole.TransformToVisual(this) is not { } m) return default;
        IBrush? fill;
        CornerRadius corners;
        switch (hole)
        {
            case GlassPanel glass when !glass.EffectiveGlassActive && glass.Fade >= 1:
                fill = glass.Background;
                corners = glass.CornerRadius;
                break;
            case Border border:
                fill = border.Background;
                corners = border.CornerRadius;
                break;
            default:
                return default;
        }
        if (fill is not ISolidColorBrush { Color.A: 255 } solid || solid.Opacity < 1) return default;
        for (Visual? v = hole; v is not null && !ReferenceEquals(v, this); v = v.GetVisualParent())
            if (v.Opacity < 1) return default;
        var radius = Math.Max(Math.Max(corners.TopLeft, corners.TopRight), Math.Max(corners.BottomLeft, corners.BottomRight));
        var bounds = new Rect(hole.Bounds.Size).TransformToAABB(m);
        var rect = inner ? bounds.Deflate(radius + 1) : bounds;
        return rect.Width > 0 && rect.Height > 0 ? rect : default;
    }

    /// <summary>Target's host is hidden while the stand-in shows, or while the opaque snapshot
    /// (Liquid Glass on) covers it: nothing of it can show, so the compositor need not draw it.</summary>
    private void ApplyHostHidden()
    {
        var hide = TargetHost is not null && (_standingIn || IsOpaqueCover);
        if (hide == _hostHidden || TargetHost is not { } host) return;
        _hostHidden = hide;
        if (hide)
        {
            _hostOpacityWasSet = host.IsSet(OpacityProperty);
            _hostOpacityWas = host.Opacity;
            host.Opacity = 0;
        }
        else if (_hostOpacityWasSet) host.Opacity = _hostOpacityWas;
        else host.ClearValue(OpacityProperty);
    }

    /// <summary>Device pixels per layout unit at Target: the window's scaling times any scale
    /// above it (Interface Size).</summary>
    private static double DeviceScale(Visual target, TopLevel top)
    {
        var scale = top.RenderScaling;
        if (target.TransformToVisual(top) is { } m)
        {
            var s = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            if (s > 0 && double.IsFinite(s)) scale *= s;
        }
        return scale;
    }

    /// <summary>Freed after the next render: the last frame drawn may still use it.</summary>
    private static void FreeLater(ref Bitmap? bitmap)
    {
        if (bitmap is { } old) Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        bitmap = null;
    }

    /// <summary>
    /// Liquid Glass switched from the open sheet: change mode in place. On: the live blur
    /// gives way to a snapshot (the plain dim until it lands, as at an open), taken once the
    /// glass look has rendered. Off: the snapshot is freed and the live blur takes over.
    ///
    /// Owner 11-26 "a weird flicker" on the switch: on, the blur dropped at once to the sharp app
    /// under the full dim until the snapshot landed, then faded back; off, the dim jumped to full
    /// and faded back down (Release's plain dim, then the live layers' lighter one). Now, standing
    /// in (the usual state with the sheet up), on keeps the stand-in's blur up, retaken with the
    /// glass look, until the snapshot replaces it in place; off fades the glass-look snapshot out
    /// over the live blur of the new look (<see cref="ModeSwitchDuration"/>), the dim unchanged.
    /// </summary>
    private void OnAppGlassChanged(object? sender, EventArgs e)
    {
        if (!_live && _snapshot is null) return; // closed: the next open picks the mode
        if (WantsLive == _live) return;
        if (_live)
        {
            // Standing in, Target is already rendered on its own without its effect, so the
            // snapshot can be taken from it while the stand-in still shows the blur.
            if (_standingIn)
            {
                LayoutBands(); // a sheet gone glass leaves no opaque hole: the bands cover it all
                RequestRetake();
                _ = HandOverToSnapshotAsync(_epoch, ++_handOverStamp);
                return;
            }
            StopLive();
            _blurHidden = false;
            // Glass back on in the middle of the off fade: that fade is over. Its finisher only
            // clears the flag while its own bitmap is still the one fading.
            _handingOff = false;
            SetLayers(imageOpacity: 0, dimOpacity: 1, animate: false);
            _ = CaptureAfterRenderAsync(_epoch);
        }
        else if (_snapshot is { } old && ReferenceEquals(_image.Source, old) && _image.Opacity > 0
                 && IsEffectivelyVisible && Opacity > 0)
            FadeSnapshotIntoLive(old);
        else
        {
            Release();
            StartLive();
        }
    }

    /// <summary>Glass on while standing in: once the glass look has rendered, the snapshot is
    /// taken (under the stand-in, which covers the whole layer meanwhile) and then shown at full
    /// strength as the stand-in goes, in one pass, so no frame shows the app unblurred.</summary>
    private async Task HandOverToSnapshotAsync(int epoch, int stamp)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        if (!Current()) return;
        var shown = await CaptureAsync();
        if (!Current())
        {
            // Glass went off again meanwhile: live stays, the snapshot it no longer needs goes.
            if (shown && epoch == _epoch && _live && WantsLive && _snapshot is { } stale)
            {
                _image.Source = null;
                _snapshot = null;
                _source = null;
                stale.Dispose();
                SetLiveLayers(animate: false);
            }
            return;
        }
        StopLive();
        _blurHidden = false;
        if (shown && _snapshot != null)
            SetLayers(imageOpacity: 1, dimOpacity: Math.Clamp(BlurDimOpacity, 0, 1), animate: false);
        else
            SetLayers(imageOpacity: 0, dimOpacity: 1, animate: true);

        bool Current() => epoch == _epoch && stamp == _handOverStamp && _live && !WantsLive && IsVisible;
    }

    /// <summary>Glass off with the snapshot up: live mode starts under it (Target blurred in
    /// place, the dim already the live one's), and the snapshot fades out over it, then is freed
    /// and the stand-in may take over. As <see cref="Release"/>, but the snapshot stays on screen.</summary>
    private void FadeSnapshotIntoLive(WriteableBitmap old)
    {
        _epoch++;
        _capture = null;
        _refreshStamp++;
        _refreshPending = false;
        _snapshot = null;
        _source = null;
        _reblurBuffer = null;
        _fadingOut.Source = null;
        _fadingBitmap?.Dispose();
        _fadingBitmap = old;
        _blurHidden = false;
        _handingOff = true;
        StartLive(); // SetLayers fades the image out over ModeSwitchDuration while _handingOff
        _ = FinishHandOffAsync(old);
    }

    private async Task FinishHandOffAsync(Bitmap old)
    {
        await Task.Delay(ModeSwitchDuration + TimeSpan.FromMilliseconds(60));
        if (!ReferenceEquals(_fadingBitmap, old)) return; // released or replaced meanwhile
        if (ReferenceEquals(_image.Source, old)) _image.Source = null;
        _fadingBitmap = null;
        old.Dispose();
        _handingOff = false;
        UpdateStandIn();
    }

    private async Task CaptureAfterRenderAsync(int epoch)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        if (epoch == _epoch && !_live && _snapshot is null && IsVisible) await CaptureAsync();
    }

    private void SetLayers(double imageOpacity, double dimOpacity, bool animate)
    {
        _image.Transitions = animate ? (_handingOff ? _modeSwitchFade : _imageFade) : null;
        _dim.Transitions = animate ? _dimFade : null;
        _image.Opacity = imageOpacity;
        _dim.Opacity = dimOpacity;
        UpdateOpaqueCover();
    }
}

/// <summary>
/// Draws <see cref="SourceRect"/> (pixels) of <see cref="Source"/> into its bounds. Not an Image:
/// a render taken above 1x (125% scaling, Interface Size) is a high-DPI bitmap, and Image hands
/// the renderer its source rect in layout units, which the renderer reads as pixels (measured: a
/// held frame came out 1.25x too large at 125%). Each new drawing tells <see cref="Probe"/> that
/// the frame it first shows in is this layer's own doing, not a change behind it.
/// </summary>
internal sealed class BandFrame : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<BandFrame, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<Rect> SourceRectProperty =
        AvaloniaProperty.Register<BandFrame, Rect>(nameof(SourceRect));

    static BandFrame() => AffectsRender<BandFrame>(SourceProperty, SourceRectProperty);

    public BandFrame() => IsHitTestVisible = false;

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Rect SourceRect { get => GetValue(SourceRectProperty); set => SetValue(SourceRectProperty, value); }
    internal ChangeProbe? Probe { get; init; }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var src = SourceRect;
        if (src.Width <= 0 || src.Height <= 0) src = new Rect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        context.DrawImage(bitmap, src, new Rect(Bounds.Size));
        if (Probe is { } probe) context.Custom(new MarkOp(new Rect(Bounds.Size), probe));
    }

    /// <summary>Render thread: the first replay of this drawing marks its frame as self-made.</summary>
    private sealed class MarkOp : ICustomDrawOperation
    {
        private readonly ChangeProbe _probe;
        private int _replayed;
        public MarkOp(Rect bounds, ChangeProbe probe) { Bounds = bounds; _probe = probe; }
        public Rect Bounds { get; }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            if (Interlocked.Exchange(ref _replayed, 1) == 0) _probe.MarkSelfFrame();
        }
    }
}

/// <summary>
/// Hears that something behind the sheet changed. Drawn over the whole layer, above the bands and
/// below the dim, it draws nothing; the compositor replays it in every frame that repaints any part
/// of the layer. A frame that repaints only the sheet's opaque surface (its own scrolling, a hover)
/// is not news, nor one in which the layer showed a new frame of its own (a band marked it); any
/// other is a change in the hidden app the stand-in must catch up with.
/// </summary>
internal sealed class ChangeProbe : Control
{
    private readonly Action _changed;
    private readonly object _gate = new();
    private Rect _hole;
    private int _selfFrame, _posted;
    /// <summary>0 off, 1 waiting for the stand-in's first frame, 2 listening.</summary>
    private int _armed;

    public ChangeProbe(Action changed)
    {
        _changed = changed;
        IsHitTestVisible = false;
    }

    /// <summary>The area (this control's coordinates) whose repaints are the sheet's own.</summary>
    internal Rect Hole
    {
        get { lock (_gate) return _hole; }
        set { lock (_gate) _hole = value; }
    }

    /// <summary>Render thread: the frame being drawn shows a new frame of the layer's own.</summary>
    internal void MarkSelfFrame() => Interlocked.Exchange(ref _selfFrame, 1);

    /// <summary>UI thread: listen from the frame that first shows the stand-in on (frames before
    /// it, the open fade's, are not changes behind the stand-in).</summary>
    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void Disarm() => Volatile.Write(ref _armed, 0);

    /// <summary>Render thread: a frame repainted <paramref name="clip"/> (device pixels).</summary>
    internal void OnFrame(SKRectI clip, SKMatrix toDevice)
    {
        if (Interlocked.Exchange(ref _selfFrame, 0) == 1)
        {
            Interlocked.CompareExchange(ref _armed, 2, 1);
            return;
        }
        if (Volatile.Read(ref _armed) != 2 || clip.IsEmpty) return;
        var hole = Hole;
        if (hole.Width > 0 && hole.Height > 0)
        {
            var device = toDevice.MapRect(new SKRect((float)hole.X, (float)hole.Y, (float)hole.Right, (float)hole.Bottom));
            // The compositor's 1 px inflation and round-out of every dirty rect (GlassBackdropCache.DirtySlack).
            device.Inflate(GlassBackdropCache.DirtySlack, GlassBackdropCache.DirtySlack);
            if (device.Contains(new SKRect(clip.Left, clip.Top, clip.Right, clip.Bottom))) return;
        }
        if (Interlocked.Exchange(ref _posted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _posted, 0);
            if (Volatile.Read(ref _armed) == 2) _changed();
        });
    }

    public override void Render(DrawingContext context) => context.Custom(new ProbeOp(new Rect(Bounds.Size), this));

    private sealed class ProbeOp : ICustomDrawOperation
    {
        private readonly ChangeProbe _owner;
        public ProbeOp(Rect bounds, ChangeProbe owner) { Bounds = bounds; _owner = owner; }
        public Rect Bounds { get; }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => other is ProbeOp o && o.Bounds == Bounds && ReferenceEquals(o._owner, _owner);
        public void Dispose() { }
        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) { _owner.OnFrame(new SKRectI(0, 0, 1, 1), SKMatrix.Identity); return; }
            using var api = lease.Lease();
            _owner.OnFrame(api.SkCanvas.DeviceClipBounds, api.SkCanvas.TotalMatrix);
        }
    }
}

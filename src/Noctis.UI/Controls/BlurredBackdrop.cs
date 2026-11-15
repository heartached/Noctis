using System;
using System.Diagnostics;
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
using Avalonia.Threading;
using Noctis.Helpers;
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

    private void UpdateOpaqueCover() =>
        IsOpaqueCover = _snapshot != null && !_live && !_blurHidden && ReferenceEquals(_image.Source, _snapshot)
                        && _image.Opacity >= 1 && Opacity >= 1 && IsVisible;

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
    /// <summary>The live blur held still (<see cref="Hold"/>): a 1:1 render of Target with its
    /// BlurEffect, under the dim, while Target itself is off the screen.</summary>
    private readonly HeldFrame _held;
    private Bitmap? _fadingBitmap;
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
        _held = new HeldFrame
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
        };
        Children.Add(_fadingOut);
        Children.Add(_image);
        Children.Add(_held);
        Children.Add(_dim);
        _image.PropertyChanged += (_, e) =>
        {
            if (e.Property == OpacityProperty || e.Property == Image.SourceProperty) UpdateOpaqueCover();
        };
        // A light/dark switch repaints the whole app under the sheet.
        ActualThemeVariantChanged += (_, _) => RefreshSoon();

        _imageFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
        _dimFade = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
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
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
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
        Release();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // The open/close fade of this layer carries the live blur with it (a held blur lets go
        // first: the fade needs the app itself under it).
        if (_live && change.Property == OpacityProperty)
        {
            ReleaseHold(animate: true);
            UpdateLiveEffect();
        }
        if (change.Property == IsVisibleProperty && !IsVisible) ReleaseHold(animate: false);
        if (change.Property == OpacityProperty || change.Property == IsVisibleProperty) UpdateOpaqueCover();
    }

    /// <summary>Puts live mode on (idempotent): the dim settles at the blur's lighter one
    /// (the plain dim at Off) and Target gets its BlurEffect for the current Opacity.</summary>
    private void StartLive()
    {
        _live = true;
        // Target fills the window, so clipping it to its bounds changes nothing on screen; but a
        // render of it on its own (the held frame, see Hold) culls what lies outside it the way
        // the window does. Without the clip, content past the window's edge (a drawer parked
        // off-screen, a row below the fold) was blurred into the held frame's edges.
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
    }

    /// <summary>Target's BlurEffect at the Background Blur strength, scaled by this layer's
    /// Opacity; none at all (no offscreen layer) when that is under half a pixel.</summary>
    private void UpdateLiveEffect()
    {
        if (!_live || Target is not { } target) return;
        // Held: Target carries no effect until the hold lets go (which comes back here).
        if (_isHeld) return;
        var radius = LiveRadiusFor(BackdropSnapshot.BlurRadius) * Math.Clamp(Opacity, 0, 1);
        if (radius < 0.5) radius = 0;
        if (radius == LiveEffectRadius && ReferenceEquals(target.Effect, _liveEffect)) return;
        LiveEffectRadius = radius;
        _liveEffect = radius > 0 ? new ImmutableBlurEffect(radius) : null;
        target.Effect = _liveEffect;
    }

    /// <summary>Takes the live blur off Target (only the effect this layer put there).</summary>
    private void StopLive()
    {
        ReleaseHold(animate: false);
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

    // ── Holding the live blur still while the sheet moves ──

    /// <summary>How long after the last <see cref="Hold"/> the blur goes live again.</summary>
    public static readonly TimeSpan HoldReleaseDelay = TimeSpan.FromMilliseconds(300);

    private bool _clippedTarget, _targetClipWasSet, _targetClipWas;
    private bool _isHeld;
    private Task? _holding;
    private Bitmap? _heldBitmap;
    private bool _heldTargetOpacitySet;
    private double _heldTargetOpacity = 1;
    /// <summary>Stopwatch timestamp the hold lasts until; 0 = none.</summary>
    private long _holdUntil;
    private bool _holdTimerRunning;
    /// <summary>Bumped whenever a hold ends or the live mode stops: a snapshot or timer that
    /// started before it is stale.</summary>
    private int _holdEpoch;

    /// <summary>The live blur is held still, how many holds took, and the held frame (tests).</summary>
    internal bool IsHeld => _isHeld;
    internal int HoldCount { get; private set; }
    internal HeldFrame HeldImage => _held;

    /// <summary>
    /// Owner 11-14: scrolling the Settings sheet over the live blur was choppy. Avalonia repaints
    /// a visual with an Effect whole whenever anything inside it changes
    /// (ServerCompositionVisual.Update: "add the entire node's old subtree bounds as a dirty
    /// region"), so with lyrics, a playing title or the progress bar moving behind the sheet the
    /// whole window was re-rendered and re-blurred every frame, on top of the scroll.
    ///
    /// While the sheet moves, call this (each scroll step; a page switch for its glide's length
    /// in <paramref name="busyFor"/>): the live blur is swapped for a render of Target taken now,
    /// effect and all, at the screen's own pixel density, so it is the frame that was on screen.
    /// Target drops its effect and goes to Opacity 0, which the compositor skips: nothing behind
    /// the sheet is drawn until <see cref="HoldReleaseDelay"/> after the last call, when the blur
    /// goes live again and the held frame fades out over it. Live mode at rest only (full
    /// opacity, blur on); otherwise a no-op.
    /// </summary>
    public void Hold(TimeSpan busyFor = default)
    {
        if (!CanHold) return;
        var until = Stopwatch.GetTimestamp() + (long)((busyFor + HoldReleaseDelay).TotalSeconds * Stopwatch.Frequency);
        if (until > _holdUntil) _holdUntil = until;
        if (!_isHeld && _holding is null) _holding = TakeHoldAsync(_holdEpoch);
        if (!_holdTimerRunning) _ = HoldTimerAsync(_holdEpoch);
    }

    /// <summary>
    /// Holds the blur while any ScrollViewer inside <paramref name="sheet"/> scrolls (wheel,
    /// scrollbar, touch, inertia, keys: anything that moves an offset).
    /// </summary>
    public void HoldWhileScrolling(Avalonia.Interactivity.Interactive sheet) =>
        sheet.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.OffsetDelta != default) Hold();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);

    private bool CanHold =>
        _live && !_blurHidden && LiveEffectRadius > 0 && Opacity >= 1 && IsEffectivelyVisible
        && Target is { } target && (_isHeld || ReferenceEquals(target.Effect, _liveEffect));

    private bool HoldExpired => _holdUntil == 0 || Stopwatch.GetTimestamp() >= _holdUntil;

    private async Task TakeHoldAsync(int epoch)
    {
        Bitmap? bitmap = null;
        try
        {
            if (Target is not { } target || TopLevel.GetTopLevel(this) is not { } top
                || ElementComposition.GetElementVisual(target) is not { } visual
                || target.Bounds.Width < 1 || target.Bounds.Height < 1)
                return;
            bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, DeviceScale(target, top));
            if (epoch != _holdEpoch || HoldExpired || !CanHold || target.TranslatePoint(default, this) is not { } at)
                return;
            _held.Transitions = null;
            _held.Opacity = 1;
            _held.Margin = new Thickness(at.X, at.Y, 0, 0);
            _held.Width = target.Bounds.Width;
            _held.Height = target.Bounds.Height;
            _held.Source = bitmap;
            _held.IsVisible = true;
            FreeHeldBitmap();
            _heldBitmap = bitmap;
            bitmap = null;
            _heldTargetOpacitySet = target.IsSet(OpacityProperty);
            _heldTargetOpacity = target.Opacity;
            target.Effect = null;
            target.Opacity = 0;
            _isHeld = true;
            HoldCount++;
        }
        catch (Exception ex)
        {
            // A backend that can't snapshot stays live, as before.
            DebugLogger.Warn(DebugLogger.Category.UI, "BlurredBackdrop.Hold", ex.Message);
        }
        finally
        {
            bitmap?.Dispose();
            if (epoch == _holdEpoch) _holding = null;
        }
    }

    /// <summary>Device pixels per layout unit at Target: the window's scaling times any scale
    /// above it (Interface Size), so the held frame is drawn 1:1.</summary>
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

    /// <summary>Task.Delay (resumed on the UI thread), as the rest of this layer: waits until
    /// <see cref="HoldReleaseDelay"/> has passed since the last <see cref="Hold"/>.</summary>
    private async Task HoldTimerAsync(int epoch)
    {
        _holdTimerRunning = true;
        while (true)
        {
            var left = _holdUntil - Stopwatch.GetTimestamp();
            if (left <= 0) break;
            await Task.Delay(TimeSpan.FromSeconds(left / (double)Stopwatch.Frequency) + TimeSpan.FromMilliseconds(1));
            if (epoch != _holdEpoch) return; // ended meanwhile; ReleaseHold reset the flag
        }
        _holdTimerRunning = false;
        ReleaseHold(animate: true);
    }

    /// <summary>
    /// Lets the blur go live again: Target gets its opacity and effect back, and the held frame
    /// fades out over it (<paramref name="animate"/>: the content behind moved meanwhile) or goes
    /// at once (the layer is closing or changing mode). Ends any pending hold too.
    /// </summary>
    private void ReleaseHold(bool animate)
    {
        _holdEpoch++;
        _holdUntil = 0;
        _holding = null;
        _holdTimerRunning = false;
        if (!_isHeld)
        {
            // A held frame still fading out from an earlier release goes at once when asked to.
            if (!animate && _held.IsVisible) HideHeld();
            return;
        }
        _isHeld = false;
        if (Target is { } target)
        {
            if (_heldTargetOpacitySet) target.Opacity = _heldTargetOpacity;
            else target.ClearValue(OpacityProperty);
            if (_live) target.Effect = _liveEffect;
        }
        UpdateLiveEffect();
        if (animate && IsEffectivelyVisible)
        {
            var bitmap = _heldBitmap;
            _held.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = CrossfadeDuration, Easing = new CubicEaseOut() } };
            _held.Opacity = 0;
            _ = HideHeldAfterFadeAsync(bitmap);
        }
        else
            HideHeld();
    }

    private async Task HideHeldAfterFadeAsync(Bitmap? bitmap)
    {
        await Task.Delay(CrossfadeDuration + TimeSpan.FromMilliseconds(60));
        if (_isHeld || !ReferenceEquals(bitmap, _heldBitmap)) return; // held again meanwhile
        HideHeld();
    }

    private void HideHeld()
    {
        _held.Transitions = null;
        _held.IsVisible = false;
        _held.Source = null;
        FreeHeldBitmap();
    }

    /// <summary>Freed after the next render: the last frame drawn may still use it.</summary>
    private void FreeHeldBitmap()
    {
        if (_heldBitmap is { } old)
            Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        _heldBitmap = null;
    }

    /// <summary>
    /// Liquid Glass switched from the open sheet: change mode in place. On: the live blur
    /// gives way to a snapshot (the plain dim until it lands, as at an open), taken once the
    /// glass look has rendered. Off: the snapshot is freed and the live blur takes over.
    /// </summary>
    private void OnAppGlassChanged(object? sender, EventArgs e)
    {
        if (!_live && _snapshot is null) return; // closed: the next open picks the mode
        if (WantsLive == _live) return;
        if (_live)
        {
            StopLive();
            _blurHidden = false;
            SetLayers(imageOpacity: 0, dimOpacity: 1, animate: false);
            _ = CaptureAfterRenderAsync(_epoch);
        }
        else
        {
            Release();
            StartLive();
        }
    }

    private async Task CaptureAfterRenderAsync(int epoch)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        if (epoch == _epoch && !_live && _snapshot is null && IsVisible) await CaptureAsync();
    }

    private void SetLayers(double imageOpacity, double dimOpacity, bool animate)
    {
        _image.Transitions = animate ? _imageFade : null;
        _dim.Transitions = animate ? _dimFade : null;
        _image.Opacity = imageOpacity;
        _dim.Opacity = dimOpacity;
        UpdateOpaqueCover();
    }
}

/// <summary>
/// Draws <see cref="Source"/> whole into its bounds. Not an Image: a snapshot taken above 1x
/// (125% scaling, Interface Size) is a high-DPI bitmap, and Image hands the renderer its source
/// rect in layout units, which the renderer reads as pixels, so only the top-left part of the
/// frame was drawn, stretched (measured: the held frame came out 1.25x too large at 125%).
/// </summary>
internal sealed class HeldFrame : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<HeldFrame, Bitmap?>(nameof(Source));

    static HeldFrame() => AffectsRender<HeldFrame>(SourceProperty);

    public HeldFrame() => IsHitTestVisible = false;

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Source is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var pixels = bitmap.PixelSize;
        context.DrawImage(bitmap, new Rect(0, 0, pixels.Width, pixels.Height), new Rect(Bounds.Size));
    }
}

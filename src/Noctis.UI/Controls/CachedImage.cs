using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// An Image control that loads artwork asynchronously using the shared LRU cache.
/// Cache hits are instant (no UI thread blocking). Cache misses load on a
/// background thread, preventing scroll stutter in virtualized lists.
/// A generation counter discards stale results when the control is recycled.
///
/// Memory: <see cref="DecodeWidth"/> is a ceiling, not the size that is decoded.
/// Once the control has a size, it asks the cache for the smallest width bucket
/// that covers its own width in device pixels (logical width × render scaling), so
/// a 180px album tile on a 2× display decodes at 384px (0.6 MB) instead of the
/// 768px (2.4 MB) it used to. The bitmap it shows is <see cref="ArtworkCache.Acquire"/>d
/// while on screen and released when the control leaves the visual tree, which is
/// what lets the cache dispose evicted bitmaps instead of leaving them to the
/// finalizer — pages kept alive by the view cache no longer pin their covers.
/// </summary>
public class CachedImage : Image
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<CachedImage, string?>(nameof(SourcePath));

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<CachedImage, int>(nameof(DecodeWidth), 512);

    /// <summary>
    /// Largest decode width this surface will ask for. The actual request is the
    /// smaller of this and the control's own width in device pixels (bucketed), once
    /// the control has been arranged.
    /// </summary>
    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public static readonly StyledProperty<bool> FastDownscaleProperty =
        AvaloniaProperty.Register<CachedImage, bool>(nameof(FastDownscale));

    /// <summary>
    /// Draw with plain bilinear sampling when the bitmap lands at 0.5–1 device pixels per
    /// bitmap pixel — what a grid tile always does, since it decodes the smallest width
    /// bucket that covers its slot (a 318px album tile shows a 384px decode).
    ///
    /// A scroll repaints every cover on every frame, and on the software renderer the
    /// sampling was most of that frame: 15 Albums covers at HighQuality (MediumQuality
    /// measured the same) cost ~17 ms of a 25 ms frame, LowQuality ~3 ms (headless Skia,
    /// 09-24). At this ratio the two are indistinguishable (46 dB PSNR, same sharpness on the
    /// Albums grid) — the decode already did the high-quality shrink. Stronger downscales (a
    /// 128px decode in a 36px row thumbnail) and upscales keep the configured mode. Opt-in,
    /// set by the tile style in App.axaml: surfaces under a 3D transform (Cover Flow) must
    /// not use it.
    /// </summary>
    public bool FastDownscale
    {
        get => GetValue(FastDownscaleProperty);
        set => SetValue(FastDownscaleProperty, value);
    }

    public static readonly StyledProperty<bool> PixelExactProperty =
        AvaloniaProperty.Register<CachedImage, bool>(nameof(PixelExact));

    /// <summary>
    /// For a single large cover (the album page header, the lyrics page, the artist portrait,
    /// the mini player): decode at exactly the device pixels the cover is drawn at — no width
    /// bucket, and no <see cref="DecodeWidth"/> cap once arranged — so the renderer draws it
    /// 1:1 instead of resampling it a second time.
    ///
    /// That second pass is what made the album header softer than the full-size viewer: the
    /// 340 DIP header asked for the 512 bucket, which at 125% was shrunk to 425 px, at 150% to
    /// 510 px (a 0.4% shrink drifts the sampling phase across the whole cover: the softest case
    /// of all), and from 175% up was stretched to 595–680 px because 512 was also the cap.
    /// Rendered through Avalonia's Skia renderer and compared with a Lanczos resize of the
    /// source (10 real covers, 10-01): +2.2 dB at 125%, +4.3 at 150%, +7.5 at 200%, and 84–87%
    /// of the reference's edge contrast where the old path kept 40–70%. The 1:1 draw needs no
    /// sampling change: Avalonia.Skia 12 only uses its cubic filter when the bitmap is enlarged
    /// (otherwise trilinear, which copies whole pixels).
    ///
    /// A size change (window resize, render scaling, a Viewbox around the image) re-decodes
    /// once the size has held for <see cref="ExactResizeDelay"/>; meanwhile the current bitmap
    /// is drawn resampled. Not for list tiles: every slot size would be a decode of its own
    /// instead of a shared bucket.
    /// </summary>
    public bool PixelExact
    {
        get => GetValue(PixelExactProperty);
        set => SetValue(PixelExactProperty, value);
    }

    /// <summary>How long a <see cref="PixelExact"/> image's size must hold before it re-decodes. Internal for tests.</summary>
    internal static TimeSpan ExactResizeDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public static readonly StyledProperty<bool> ClearOnSourceChangeProperty =
        AvaloniaProperty.Register<CachedImage, bool>(nameof(ClearOnSourceChange), defaultValue: true);

    /// <summary>
    /// Whether to blank the image while a new source is decoding (default true).
    ///
    /// True is correct in virtualized lists: a recycled container must not keep showing
    /// the previous item's cover. Set it to false on single-item surfaces — the player
    /// bar, the lyrics page — where holding the old cover across a track change avoids a
    /// visible placeholder flash.
    /// </summary>
    public bool ClearOnSourceChange
    {
        get => GetValue(ClearOnSourceChangeProperty);
        set => SetValue(ClearOnSourceChangeProperty, value);
    }

    private int _loadGeneration;
    private readonly object _generationLock = new();
    /// <summary>The cache bitmap currently acquired on behalf of this control.</summary>
    private Bitmap? _held;
    /// <summary>The artwork path <see cref="_held"/> was loaded for.</summary>
    private string? _heldPath;
    /// <summary>Width the current load was sized for; a later size change that needs a bigger bucket reloads.</summary>
    private int _loadedWidth;
    private bool _attached;
    /// <summary>Debounces a <see cref="PixelExact"/> re-decode while the size is still changing.</summary>
    private DispatcherTimer? _exactResizeTimer;
    /// <summary>Nearest Viewbox ancestor of a <see cref="PixelExact"/> image, while attached.</summary>
    private Viewbox? _viewbox;

    static CachedImage()
    {
        SourcePathProperty.Changed.AddClassHandler<CachedImage>((img, _) => img.OnSourcePathChanged());
        DecodeWidthProperty.Changed.AddClassHandler<CachedImage>((img, _) => img.OnSourcePathChanged());
        PixelExactProperty.Changed.AddClassHandler<CachedImage>((img, _) => img.OnSourcePathChanged());
    }

    /// <summary>The mode the XAML configured, while <see cref="FastDownscale"/> has swapped in LowQuality.</summary>
    private BitmapInterpolationMode? _configuredInterpolation;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == BoundsProperty
            || change.Property == FastDownscaleProperty || change.Property == StretchProperty)
            UpdateInterpolation();
    }

    /// <summary>
    /// Image.Render is sealed, so the sampling choice is made on the control's own render
    /// options: LowQuality while the current bitmap is a mild downscale, the configured mode
    /// otherwise (an upscaled fallback bitmap, a hidden tile, the property off).
    /// </summary>
    private void UpdateInterpolation()
    {
        var fast = FastDownscale && Source is Bitmap bitmap
            && IsMildDownscale(Bounds.Size, bitmap.Size, bitmap.PixelSize, Stretch, StretchDirection,
                (VisualRoot as TopLevel)?.RenderScaling ?? 1.0);
        if (fast)
        {
            if (_configuredInterpolation != null) return;
            var configured = RenderOptions.GetBitmapInterpolationMode(this);
            // None is a deliberate choice (pixel art); LowQuality already is the cheap path.
            if (configured is BitmapInterpolationMode.None or BitmapInterpolationMode.LowQuality) return;
            _configuredInterpolation = configured;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
        }
        else if (_configuredInterpolation is { } configured)
        {
            _configuredInterpolation = null;
            RenderOptions.SetBitmapInterpolationMode(this, configured);
        }
    }

    /// <summary>
    /// True when the bitmap is drawn at 0.5–1 device pixels per bitmap pixel on both axes:
    /// a shrink bilinear filtering handles without aliasing (see <see cref="FastDownscale"/>).
    /// </summary>
    internal static bool IsMildDownscale(Size bounds, Size bitmapSize, PixelSize bitmapPixels,
        Stretch stretch, StretchDirection direction, double renderScaling)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bitmapPixels.Width <= 0 || bitmapPixels.Height <= 0)
            return false;
        var scale = stretch.CalculateScaling(bounds, bitmapSize, direction);
        var densityX = scale.X * renderScaling * bitmapSize.Width / bitmapPixels.Width;
        var densityY = scale.Y * renderScaling * bitmapSize.Height / bitmapPixels.Height;
        // 1.01: layout rounding can land a 1:1 draw a hair above one.
        return densityX is >= 0.5 and <= 1.01 && densityY is >= 0.5 and <= 1.01;
    }

    /// <summary>
    /// The width a <see cref="PixelExact"/> decode needs to land 1:1: the slot in device pixels,
    /// through the stretch, for square art (covers are; one that is not is drawn resampled
    /// with the configured mode, as before). Filling a tall slot takes its height; a whole
    /// cover in a wide slot only its height.
    /// </summary>
    internal static int ExactDecodeWidth(Size slot, double deviceScale, Stretch stretch)
    {
        var width = slot.Width;
        if (slot.Height > 0)
        {
            if (stretch == Stretch.UniformToFill) width = Math.Max(width, slot.Height);
            else if (stretch == Stretch.Uniform) width = Math.Min(width, slot.Height);
        }
        // Layout rounding puts the slot on whole device pixels; the tolerance keeps float noise
        // (0.1 × 3 = 0.30000000000000004) from asking for a pixel more than is drawn.
        var device = (int)Math.Ceiling(width * deviceScale - 0.001);
        return Math.Clamp(device, 1, ArtworkCache.MaxExactWidth);
    }

    /// <summary>
    /// Scale the nearest Viewbox ancestor draws this image at (the lyrics page's disc, vinyl
    /// and cassette costumes: a 200-unit canvas stretched to the slot); 1 without one.
    /// </summary>
    private double ViewboxScale()
    {
        if (_viewbox is not { Child: { } child } viewbox) return 1;
        var natural = child.Bounds.Size;
        if (natural.Width <= 0 || natural.Height <= 0 || viewbox.Bounds.Width <= 0 || viewbox.Bounds.Height <= 0)
            return 1;
        var scale = viewbox.Stretch.CalculateScaling(viewbox.Bounds.Size, natural, viewbox.StretchDirection);
        return Math.Max(scale.X, scale.Y);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ArtworkCache.Invalidated += OnArtworkInvalidated;
        if (PixelExact)
        {
            // A Viewbox rescales without re-arranging what is inside it: watch it directly.
            _viewbox = this.FindAncestorOfType<Viewbox>();
            if (_viewbox != null) _viewbox.SizeChanged += OnViewboxSizeChanged;
        }
        // Re-acquire what a detach released (a cached page coming back, a recycled row).
        if (Source is null && !string.IsNullOrEmpty(SourcePath))
            OnSourcePathChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ArtworkCache.Invalidated -= OnArtworkInvalidated;
        _attached = false;
        _exactResizeTimer?.Stop();
        if (_viewbox != null)
        {
            _viewbox.SizeChanged -= OnViewboxSizeChanged;
            _viewbox = null;
        }
        // Let go of the bitmap while off screen. Pages parked by CachedViewLocator kept
        // every one of their covers reachable (and therefore never disposed) this way.
        lock (_generationLock) { _loadGeneration++; }
        SetSource(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Width of the slot the control was last arranged into, in logical px.</summary>
    private double _slotWidth;
    /// <summary>Height of that slot, in logical px (a <see cref="PixelExact"/> fill of a tall slot needs it).</summary>
    private double _slotHeight;
    /// <summary>Render scaling at that arrange.</summary>
    private double _arrangedScaling;

    /// <summary>
    /// An Image with no Source arranges itself to 0×0, so Bounds never says how wide
    /// the slot is before the first bitmap lands. The arrange size does; record it
    /// here and size the decode request from it. Layout is not changed.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = double.IsNaN(finalSize.Width) || double.IsInfinity(finalSize.Width) ? 0 : finalSize.Width;
        var height = double.IsNaN(finalSize.Height) || double.IsInfinity(finalSize.Height) ? 0 : finalSize.Height;
        // The scaling counts too: a window moved to a monitor with another one re-arranges
        // at the same logical size and needs a decode of another pixel size.
        var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        var changed = width != _slotWidth || height != _slotHeight || scaling != _arrangedScaling;
        _slotWidth = width;
        _slotHeight = height;
        _arrangedScaling = scaling;
        var result = base.ArrangeOverride(finalSize);
        if (width > 0 && (_pendingLoad || _loadedForUnknownSize || (changed && !string.IsNullOrEmpty(SourcePath))))
            Dispatcher.UIThread.Post(OnArranged, DispatcherPriority.Loaded);
        return result;
    }

    private void OnArranged()
    {
        var path = SourcePath;
        if (!_attached || string.IsNullOrEmpty(path) || _slotWidth <= 0) return;
        if (_pendingLoad)
        {
            // First arrange: start the load that was waiting for a width.
            int generation;
            lock (_generationLock) { generation = _loadGeneration; }
            LoadCore(path, generation);
            return;
        }
        if (_loadedForUnknownSize)
        {
            // The safety-net load ran before the first arrange. Now that the real
            // width is known, fetch that size; the cap-sized bitmap is just a cache
            // entry that ages out.
            _loadedForUnknownSize = false;
            if (RequestedWidth() != _loadedWidth)
                OnSourcePathChanged();
            return;
        }
        if (PixelExact)
        {
            // Any change: a bitmap bigger than the slot is resampled as much as a smaller one.
            // Waits for the size to hold, so a window drag decodes once, at the size it ends on.
            if (RequestedWidth() != _loadedWidth)
            {
                if (_exactResizeTimer == null)
                {
                    _exactResizeTimer = new DispatcherTimer();
                    _exactResizeTimer.Tick += OnExactResizeTick;
                }
                _exactResizeTimer.Stop();
                _exactResizeTimer.Interval = ExactResizeDelay;
                _exactResizeTimer.Start();
            }
            return;
        }
        // A grow past the bucket it was decoded for (e.g. the album tile size
        // slider, a window resize on a stretched cover): fetch the right size.
        if (RequestedWidth() > _loadedWidth)
            OnSourcePathChanged();
    }

    private void OnExactResizeTick(object? sender, EventArgs e)
    {
        _exactResizeTimer?.Stop();
        if (_attached && !string.IsNullOrEmpty(SourcePath) && RequestedWidth() != _loadedWidth)
            OnSourcePathChanged();
    }

    private void OnViewboxSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_slotWidth > 0)
            Dispatcher.UIThread.Post(OnArranged, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Decode width to ask the cache for: the control's slot width in device pixels,
    /// bucketed, capped by <see cref="DecodeWidth"/>; for <see cref="PixelExact"/>, the exact
    /// device width it is drawn at (<see cref="ExactDecodeWidth"/>). Falls back to the cap
    /// while the control has not been arranged.
    /// </summary>
    internal int RequestedWidth()
    {
        var cap = ArtworkCache.NormalizeDecodeWidth(DecodeWidth);
        if (_slotWidth <= 0) return cap;
        var scale = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        if (PixelExact)
            return ExactDecodeWidth(new Size(_slotWidth, _slotHeight), scale * ViewboxScale(), Stretch);
        var device = (int)Math.Ceiling(_slotWidth * scale);
        return Math.Min(cap, ArtworkCache.NormalizeDecodeWidth(device));
    }

    private void SetSource(Bitmap? bitmap, string? path = null)
    {
        _heldPath = bitmap is null ? null : path;
        if (ReferenceEquals(bitmap, _held) && ReferenceEquals(Source, bitmap)) return;
        if (bitmap is not null) ArtworkCache.Acquire(bitmap);
        var previous = _held;
        _held = bitmap;
        Source = bitmap;
        if (previous is not null) ArtworkCache.Release(previous);
    }

    private void OnArtworkInvalidated(string path)
    {
        // Invalidated can be raised from any thread; SourcePath is an
        // AvaloniaProperty and may only be read on the UI thread, so the
        // comparison has to happen inside the post, not before it.
        Dispatcher.UIThread.Post(() =>
        {
            if (string.Equals(SourcePath, path, StringComparison.Ordinal))
                OnSourcePathChanged();
        });
    }

    private void OnSourcePathChanged()
    {
        var path = SourcePath;
        int generation;
        lock (_generationLock)
        {
            generation = ++_loadGeneration;
        }

        if (string.IsNullOrEmpty(path))
        {
            SetSource(null);
            _loadedWidth = 0;
            return;
        }

        // Off the tree: OnAttachedToVisualTree restarts the load, and a load now
        // would only size itself for the cap.
        if (!_attached) return;

        // A container in a virtualized list gets its DataContext (and so its
        // SourcePath) before it is arranged. Loading right away would size the
        // request for the cap; the first arrange (OnArranged) starts the load with
        // the real width instead, in the same frame. The Background post is the
        // safety net for a control that is never arranged with a width of its own:
        // it loads at the cap.
        if (_slotWidth <= 0)
        {
            _pendingLoad = true;
            Dispatcher.UIThread.Post(() =>
            {
                // Hidden (IsVisible=false somewhere up the tree) controls are never
                // arranged: leave the load pending until they are shown and get a
                // size, instead of decoding a cover nobody can see.
                if (_pendingLoad && !IsEffectivelyVisible) return;
                LoadCore(path, generation);
            }, DispatcherPriority.Background);
            return;
        }

        LoadCore(path, generation);
    }

    private bool _pendingLoad;
    private int _startedGeneration;
    /// <summary>The current load ran before the control had a width and used the cap.</summary>
    private bool _loadedForUnknownSize;

    private async void LoadCore(string path, int generation)
    {
        lock (_generationLock)
        {
            if (generation != _loadGeneration || _startedGeneration == generation) return;
            _startedGeneration = generation;
        }
        _pendingLoad = false;
        _loadedForUnknownSize = _slotWidth <= 0;

        var decodeWidth = RequestedWidth();
        _loadedWidth = decodeWidth;
        var exact = PixelExact;

        // Fast path: cache hit returns immediately, no I/O
        var cached = ArtworkCache.TryGet(path, decodeWidth, exact);
        if (cached != null)
        {
            SetSource(cached, path);
            return;
        }

        // Slow path: load on background thread to avoid blocking UI.
        //
        // Before blanking, check whether ANY width bucket holds this artwork — a decode
        // from another surface (album grid at 384, songs list at 256, …) is pixel-correct
        // for this control too, just resampled. Painting it immediately removes the
        // blank-then-pop flicker on surfaces whose own bucket is cold, e.g. the
        // Add-to-Playlist thumbnails. When that bitmap is at least as wide as we need
        // (and not absurdly wider) it IS the result: no second decode, no second copy.
        // This is safe for recycled list containers: the fallback is the NEW item's art.
        // Single-cover surfaces (ClearOnSourceChange off: the lyrics page, player, mini
        // player) take it only when it is big enough. Their cache is usually a 128-384px
        // tile decode, which stretched to a ~1000px cover showed blurry for the length of
        // the full decode and then snapped sharp. The previous cover stays up instead.
        // A new size of the cover already on screen (a resize, another scaling) is treated
        // the same way on every surface: it stays up unless the cache has one big enough.
        var showingThisCover = _held is not null && _heldPath == path;
        var fallback = ArtworkCache.TryGetAnyWidth(path, decodeWidth, out var sufficient, exact);
        if (fallback != null
            && (sufficient || (ClearOnSourceChange && !showingThisCover) || fallback.PixelSize.Width >= decodeWidth))
        {
            SetSource(fallback, path);
            if (sufficient) return;
        }
        // Keeping the previous Source visible avoids a placeholder flash on every track
        // switch — correct for the player's single cover, wrong for a virtualized list,
        // where a recycled container gets a new SourcePath and keeps rendering the
        // PREVIOUS item's art until the decode lands. Scrolling a large grid past the
        // cache budget therefore showed a trail of wrong covers.
        else if (ClearOnSourceChange && !showingThisCover)
            SetSource(null);

        try
        {
            var bitmap = await Task.Run(() => DecodeInBackground(path, decodeWidth, generation, exact));

            // Discard result if the control was recycled (SourcePath changed again)
            bool isCurrentGeneration;
            lock (_generationLock)
            {
                isCurrentGeneration = generation == _loadGeneration;
            }

            if (isCurrentGeneration)
                SetSource(bitmap, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CachedImage] Failed to load artwork '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Pool-thread half of a cache miss. Cover files are commonly 3000px PNGs, which no codec
    /// can shrink while decoding: each miss is a full 36 MB decode (~80 ms on a fast core).
    /// A wheel glide realizes a row of tiles every few frames, so misses queue faster than
    /// the pool drains them. A tile scrolled past before its turn skips the decode instead
    /// of paying for a cover nobody will see, and decodes run below normal priority so, when
    /// they saturate the cores, the UI and render threads still get theirs.
    /// </summary>
    internal Bitmap? DecodeInBackground(string path, int decodeWidth, int generation, bool exact = false)
    {
        lock (_generationLock)
        {
            if (generation != _loadGeneration) return null;
        }

        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return ArtworkCache.LoadAndCache(path, decodeWidth, exact);
        }
        finally
        {
            thread.Priority = priority;
        }
    }
}

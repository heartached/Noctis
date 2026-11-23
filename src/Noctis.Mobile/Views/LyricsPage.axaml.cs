using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class LyricsPage : UserControl
{
    /// <summary>Fraction of the viewport the active line is anchored at. Apple Music's phone
    /// lyrics (owner's reference, 10-20) sit the sung line high, right under the line just
    /// sung, which fades under the header; the desktop page's 0.32 put it near mid-screen.</summary>
    internal const double AnchorRatio = 0.22;

    /// <summary>The desktop's cascade: each line below the active one starts its glide this
    /// much later (capped at <see cref="CascadeMaxLines"/>), and may lag the line above by at
    /// most <see cref="MaxCascadeLagStepPx"/>, so the stack settles top-down without lines
    /// ever crossing (LyricsView.AnimateScroll).</summary>
    private const int CascadeMaxLines = 8;
    private const double MaxCascadeLagStepPx = 16;

    /// <summary>After the user's last touch on the lyrics, how long they stay free to read
    /// before the page glides back to the sung line. Beta (11-11): 3.5 s, restarted by every
    /// pixel a fling moved for up to 2.5 s after the finger lifted, made the wait up to 6 s;
    /// Apple Music comes back about 2 s after the scroll settles.</summary>
    internal static TimeSpan BrowseResumeDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A fling's movement restarts that wait only this long after the finger lifts:
    /// its long slow tail keeps the list browsing but no longer pushes the return back.</summary>
    internal static TimeSpan FlingExtendWindow { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the transport stays up after a touch while the song plays.</summary>
    internal static TimeSpan ControlsHideDelay { get; set; } = TimeSpan.FromSeconds(4);

    private ShellViewModel? _shell;
    private LyricsPageViewModel? _lyrics;
    private bool _frameLoopRunning;
    /// <summary>A ⋯ menu or sheet covers the page: the lines hold still and draw from a cached bitmap.</summary>
    private bool _heldForMenu;

    private bool _gliding;
    private double _glideFrom;
    private double _glideTo;
    private double _glideStartMs;
    private double _glideDurationMs;
    private List<(Control Control, double DelayMs)>? _cascade;
    private double _cascadeMaxDelayMs;

    // Every offset this page writes goes through ApplyOffset; one that lands without matching
    // came from the user's finger (a drag or its fling) and starts browsing.
    private double _lastAppliedY = double.NaN;
    private readonly DispatcherTimer _browseTimer;
    private readonly DispatcherTimer _controlsTimer;
    private readonly DispatcherTimer _controlsOffTimer;
    private readonly DispatcherTimer _holdRelease;
    private readonly DispatcherTimer _glideDone;
    // The frame callback, made once: a method group passed per frame is a new delegate per frame.
    private readonly Action<TimeSpan> _onFrame;

    public LyricsPage()
    {
        _onFrame = OnFrame;
        InitializeComponent();
        LyricsList.ContainerPrepared += (_, _) => Services.FrameStallMonitor.Count("lyric rows prepared");
        LyricsScroll.SizeChanged += (_, _) => UpdateEdgePadding();
        LyricsScroll.PropertyChanged += OnScrollPropertyChanged;

        _browseTimer = new DispatcherTimer { Interval = BrowseResumeDelay };
        _browseTimer.Tick += (_, _) => EndBrowse(scrollBack: true);
        _controlsTimer = new DispatcherTimer { Interval = ControlsHideDelay };
        _controlsTimer.Tick += (_, _) => HideControlsIfPlaying();
        _controlsOffTimer = new DispatcherTimer { Interval = ControlsFadeOut };
        _controlsOffTimer.Tick += (_, _) => RemoveHiddenControls();
        _holdRelease = new DispatcherTimer { Interval = HoldReleaseDelay };
        _holdRelease.Tick += (_, _) => { if (!WantsHold) ReleaseHold(); };
        _glideDone = new DispatcherTimer();
        _glideDone.Tick += (_, _) => OnCompositorGlideDone();

        // Any touch brings the transport back (and keeps it up a while), even one a line or
        // the scroller handles; a tap on a line also ends browsing, as the seek it makes
        // should be followed.
        AddHandler(PointerPressedEvent, (_, _) => ShowControls(), RoutingStrategies.Tunnel, handledEventsToo: true);
        LyricsList.AddHandler(Button.ClickEvent, (_, _) => EndBrowse(scrollBack: false), RoutingStrategies.Bubble);
        LyricsScroll.AddHandler(PointerPressedEvent, OnScrollPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        LyricsScroll.AddHandler(PointerReleasedEvent, OnScrollPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Backdrop.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) UpdateFades(); };
        BlurCover.ImageChanged += (_, _) => UpdateFades();
        TopFade.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateFades(); };
        BottomFade.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateFades(); };
        SizeChanged += (_, e) =>
        {
            ApplyOrientation(e.NewSize);
            UpdateFades();
        };
        // The lyric text follows the column's width (LyricsPageViewModel.AutoBaseFontSize).
        LyricsScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty && _shell is { } shell && LyricsScroll.Bounds.Width > 0)
                shell.Lyrics.ViewWidth = LyricsScroll.Bounds.Width;
        };

        SeekBar.ScrubStarted += (_, _) => _shell?.Player.BeginSeek();
        SeekBar.ScrubCompleted += OnSeekCompleted;
        SeekBar.ScrubCanceled += (_, _) => _shell?.Player.EndSeek();
        SeekBar.ValueChanged += OnSeekValueChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_lyrics != null)
        {
            _lyrics.PropertyChanged -= OnLyricsChanged;
            _lyrics.Lines.CollectionChanged -= OnLinesChanged;
        }
        if (_shell != null)
        {
            _shell.Player.PropertyChanged -= OnPlayerChanged;
            _shell.PropertyChanged -= OnShellChanged;
        }
        _shell = DataContext as ShellViewModel;
        _lyrics = _shell?.Lyrics;
        if (_lyrics != null)
        {
            _lyrics.PropertyChanged += OnLyricsChanged;
            _lyrics.Lines.CollectionChanged += OnLinesChanged;
        }
        if (_shell != null)
        {
            _shell.Player.PropertyChanged += OnPlayerChanged;
            _shell.PropertyChanged += OnShellChanged;
        }
        UpdateMenuHold();
        if (_lyrics != null && LyricsScroll.Bounds.Width > 0) _lyrics.ViewWidth = LyricsScroll.Bounds.Width;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        UpdateMenuHold();
        FeedTimeline();
        if (!IsVisible)
        {
            // The keep-alive stops asking now (the frame loop ends at its next frame): its frames
            // redraw the screen even under a hidden page.
            KeepAlive?.Run(false);
            EndBrowse(scrollBack: false);
            _controlsTimer.Stop();
            _controlsOffTimer.Stop();
            _fingerDown = false;
            return;
        }
        // Frames stop while the page is hidden, so the active line is stale on open: catch
        // up first, so the snap below lands on the current line (and a paused song, which
        // runs no frame loop, highlights the right one).
        _lyrics?.OnFrame(LyricsPageViewModel.NowMs());
        EnsureFrameLoop();
        // Apple's lyrics open on the lines alone while the song plays; a touch brings the
        // transport up. Paused, or with nothing to read, it is there from the start.
        ShowControls();
        HideControlsIfPlaying();
        // After layout, so the containers exist and the extent is measured.
        Dispatcher.UIThread.Post(() => ScrollToLine(_lyrics?.ScrollLineIndex ?? -1, animate: false), DispatcherPriority.Loaded);
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsPageViewModel.WantsFrames))
            EnsureFrameLoop();
        else if (e.PropertyName == nameof(LyricsPageViewModel.ScrollLineIndex) && IsVisible && !_heldForMenu)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            ScrollToLine(_lyrics!.ScrollLineIndex, animate: true);
            Services.FrameStallMonitor.AddTime("lyric scroll start", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        else if (e.PropertyName == nameof(LyricsPageViewModel.HasLyrics) && IsVisible)
            ShowControls();   // (re)starts the hide timer once there is something to read
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsMenuOpen) or nameof(ShellViewModel.IsSheetOpen)) UpdateMenuHold();
        // Android may report the insets after the resize, and the page's size does not change
        // when only they do: the landscape layout is placed from them, so it follows them too.
        else if (e.PropertyName == nameof(ShellViewModel.SafeArea)) ApplyOrientation(Bounds.Size);
        else if (e.PropertyName == nameof(ShellViewModel.IsSuspended))
        {
            // Gone to the background: the keep-alive stops now (the frame loop ends at its next
            // frame). Back in front: catch up to the sung line and stand on it, as on open (it
            // used to glide there from wherever the page left off, in front of the user), then
            // the frames run again.
            if (_shell is { IsSuspended: true }) KeepAlive.Run(false);
            else if (IsVisible)
            {
                _lyrics?.OnFrame(LyricsPageViewModel.NowMs());
                if (!_heldForMenu) ScrollToLine(_lyrics?.ScrollLineIndex ?? -1, animate: false);
                EnsureFrameLoop();
            }
        }
    }

    /// <summary>
    /// S23 at 120 Hz (11-14): the ⋯ menu popped smoothly over the Songs list but stuttered over the
    /// lyrics (24-40 frames over 25 ms per open and close): its dim and glass repaint everything
    /// under them on every frame of the pop, and here that is a column of lines each drawn through
    /// its own depth blur. While a menu or sheet covers the page the lines hold still (no frames,
    /// no glide) and the lyric column is drawn once into a bitmap at the screen's scale, which the
    /// pop then reuses. On close the cache goes and the page catches up to the sung line.
    /// </summary>
    private void UpdateMenuHold()
    {
        var hold = WantsHold;
        if (hold) _holdRelease.Stop();
        if (hold == _heldForMenu) return;
        if (hold)
        {
            _heldForMenu = true;
            StopGlide();
            var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            LyricsScroll.CacheMode = new BitmapCache { RenderAtScale = scale };
            return;
        }
        // The card shrinks away over the page for PopMenu.PopCloseMs after the menu's flag drops.
        // Released at the tap (S23, 11-17), the first frames of every close drew each blurred line
        // live under the closing glass: 3-4 frames of 24-34 ms. The lines stay held under it and
        // come back once it has gone.
        if (IsVisible && HoldReleaseDelay > TimeSpan.Zero)
        {
            if (!_holdRelease.IsEnabled)
            {
                _holdRelease.Interval = HoldReleaseDelay;
                _holdRelease.Start();
            }
            return;
        }
        ReleaseHold();
    }

    private bool WantsHold => IsVisible && _shell is { } shell && (shell.IsMenuOpen || shell.IsSheetOpen);

    /// <summary>How long the lines stay held after a menu or sheet closes: its close pop.</summary>
    internal static TimeSpan HoldReleaseDelay { get; set; } = TimeSpan.FromMilliseconds(PopMenu.PopCloseMs);

    private void ReleaseHold()
    {
        _holdRelease.Stop();
        if (!_heldForMenu) return;
        _heldForMenu = false;
        LyricsScroll.CacheMode = null;
        if (!IsVisible) return;
        _lyrics?.OnFrame(LyricsPageViewModel.NowMs());
        EnsureFrameLoop();
        ScrollToLine(_lyrics?.ScrollLineIndex ?? -1, animate: true);
    }

    /// <summary>Held under a menu (tests read it).</summary>
    internal bool IsHeldForMenu => _heldForMenu;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.IsPlaying) && IsVisible) ShowControls();
        // Between plain lines the frame loop rests; each position poll (4 a second) wakes it
        // once the next line is near (KeepsFrames), or at once after a seek.
        else if (e.PropertyName == nameof(NowPlayingViewModel.Position) && !_frameLoopRunning && IsVisible) EnsureFrameLoop();
    }

    /// <summary>A new track's lyrics start at the top.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        StopGlide();
        EndBrowse(scrollBack: false);
        ApplyOffset(0);
    }

    /// <summary>Top and bottom room so the first and last lines can reach the anchor
    /// (as the desktop: a little above, most of the viewport below).</summary>
    private void UpdateEdgePadding()
    {
        var height = LyricsScroll.Viewport.Height;
        LyricsList.Margin = new Thickness(28, height * 0.12, 28, height * 0.70);
    }

    // ── Landscape ──

    private bool? _landscape;
    private double _sideWidth = double.NaN;
    private Thickness _safe;

    /// <summary>
    /// Beta 2 (#mobile-beta): sideways, the lyrics kept the portrait column, its header and
    /// transport leaving the lines a strip in the middle. Landscape now matches Now Playing's
    /// (NowPlayingPage.ApplyOrientation): the big cover on the left, where Now Playing's
    /// stands - the same 5:6 split, 24 dp margins and full height - and the header, lines and
    /// transport on the right. The header's own small cover makes way for the big one (pulled
    /// out of its column, not hidden: the wash is made from its image). Portrait is untouched.
    /// </summary>
    private void ApplyOrientation(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        var landscape = size.Width > size.Height;
        var safe = _shell?.SafeArea ?? default;
        var side = landscape ? safe.Left + 24 + Math.Max(0, size.Width - safe.Left - safe.Right - 48) * 5 / 11 : 0;
        if (_landscape == landscape && side == _sideWidth && safe == _safe) return;
        _landscape = landscape;
        _sideWidth = side;
        _safe = safe;
        // Built, not parsed: "{side},*" formats in the phone's culture, and "444,27,*" (German,
        // French, Russian...) is three columns - the lyrics got a 27 dp one and spilled off screen.
        Split.ColumnDefinitions = landscape
            ? new ColumnDefinitions { new ColumnDefinition(side, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) }
            : new ColumnDefinitions { new ColumnDefinition(1, GridUnitType.Star) };
        Grid.SetColumn(LyricsSide, landscape ? 1 : 0);
        SideArtBox.IsVisible = landscape;
        SideArtBox.Margin = new Thickness(safe.Left + 24, safe.Top, 24, safe.Bottom + 8);
        HeaderArtButton.Opacity = landscape ? 0 : 1;
        HeaderArtButton.IsHitTestVisible = !landscape;
        HeaderArtButton.Margin = landscape ? new Thickness(-72, 0, 0, 0) : default;
        // The fades repaint the wash where they stand on the page; moving to the other column
        // changes that place but not their size, so nothing else tells them.
        Dispatcher.UIThread.Post(UpdateFades, DispatcherPriority.Loaded);
    }

    // ── Soft edges ──

    // LyricsShade's stops (alpha of black at 0, 0.5 and 1 of the page height).
    private static readonly (double At, byte Alpha)[] ShadeStops = { (0, 0x99), (0.5, 0xA6), (1, 0xB8) };

    /// <summary>The top and bottom bands repaint the wash exactly as it lies under them (the
    /// backdrop bitmap over the whole page, the shade's gradient), so they need the page size
    /// and their own place in it.</summary>
    private void UpdateFades()
    {
        var page = Bounds.Size;
        if (page.Width <= 0 || page.Height <= 0) return;
        // Blurred Cover: the bands repaint its bitmap as BlurredCoverBackdrop draws it (cropped
        // to fill). They used to fall back to the plain page colour once the wash was hidden: a
        // dark band under the header with a hard edge on the blurred cover above it (S23, 11-19).
        var blurred = BlurCover.HasImage ? BlurCover.Current : null;
        var art = blurred ?? (Backdrop.IsVisible ? Backdrop.Source as IImageBrushSource : null);
        var stretch = blurred != null ? Stretch.UniformToFill : Stretch.Fill;
        foreach (var band in new[] { TopFade, BottomFade })
        {
            if (band.TranslatePoint(new Point(0, 0), this) is not { } at) continue;
            band.Update(art, stretch, (Background as ISolidColorBrush)?.Color ?? Colors.Black, Shade, new Rect(at, band.Bounds.Size), page);
        }
    }

    internal static Color Shade(double at)
    {
        at = Math.Clamp(at, 0, 1);
        for (var i = 1; i < ShadeStops.Length; i++)
        {
            if (at > ShadeStops[i].At) continue;
            var (a0, v0) = ShadeStops[i - 1];
            var (a1, v1) = ShadeStops[i];
            var t = (at - a0) / (a1 - a0);
            return Color.FromArgb((byte)Math.Round(v0 + (v1 - v0) * t), 0, 0, 0);
        }
        return Color.FromArgb(ShadeStops[^1].Alpha, 0, 0, 0);
    }

    // ── Transport ──

    /// <summary>True while the transport is up (tests read it).</summary>
    internal bool ControlsShown => ControlsLayer.IsHitTestVisible;

    internal void ShowControls()
    {
        _controlsOffTimer.Stop();
        ControlsLayer.IsVisible = true;
        FeedTimeline();
        ControlsLayer.Opacity = 1;
        ControlsLayer.IsHitTestVisible = true;
        _controlsTimer.Stop();
        if (IsVisible && _shell?.Player.IsPlaying == true && _lyrics?.HasLyrics == true)
            _controlsTimer.Start();
    }

    /// <summary>Playing with lyrics on screen, the transport fades away as Apple Music's does;
    /// paused (or nothing to read), it stays.</summary>
    internal void HideControlsIfPlaying()
    {
        _controlsTimer.Stop();
        if (!IsVisible || _shell?.Player.IsPlaying != true || _lyrics?.HasLyrics != true || SeekBar.IsPointerOver) return;
        ControlsLayer.Opacity = 0;
        ControlsLayer.IsHitTestVisible = false;
        _controlsOffTimer.Stop();
        _controlsOffTimer.Start();
    }

    /// <summary>The fade (the style's 0.35 s) plus a beat; then the transport leaves layout.</summary>
    private static readonly TimeSpan ControlsFadeOut = TimeSpan.FromMilliseconds(450);

    /// <summary>A transport at opacity 0 still measures: its elapsed and remaining labels
    /// re-laid out the page once a second and its seek bar repainted on every position tick
    /// while the words swept, a hitch the phone felt each second. Once faded it is not visible
    /// at all (a touch shows it again).</summary>
    private void RemoveHiddenControls()
    {
        _controlsOffTimer.Stop();
        if (!ControlsShown) ControlsLayer.IsVisible = false;
        FeedTimeline();
    }

    /// <summary>The seek bar and times are fed only while they can be seen: the page shown and
    /// the transport not faded away (NowPlayingPage.FeedTimeline).</summary>
    private void FeedTimeline()
    {
        if (SeekBar is null) return;   // before InitializeComponent
        var seen = IsVisible && ControlsLayer.IsVisible;
        NowPlayingPage.FeedTimeline(SeekBar, SeekBar, seen);
        NowPlayingPage.FeedTimeline(TimeRow, SeekBar, seen);
    }

    // Seek on release only, as Now Playing (NowPlayingPage.OnSeekCompleted).
    private void OnSeekCompleted(object? sender, EventArgs e)
    {
        if (_shell == null) return;
        _shell.Player.EndSeek();
        _shell.Player.Seek(TimeSpan.FromSeconds(SeekBar.Value));
    }

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => _shell?.Player.PreviewSeek(TimeSpan.FromSeconds(e.NewValue));

    // ── Browsing (the user scrolls the lyrics) ──

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty || e.NewValue is not Vector v) return;
        if (double.IsNaN(_lastAppliedY))
        {
            _lastAppliedY = v.Y;   // the first layout pass, not a gesture
            return;
        }
        // 1 px: layout may round the offset this page asked for. Only a finger on the lyrics
        // (or the fling it left behind) counts: layout clamping the offset when the extent
        // changes, or ScrollIntoView realising a far line, must not stop the follow.
        if (Math.Abs(v.Y - _lastAppliedY) > 1.0 && (_fingerDown || LyricsPageViewModel.NowMs() - _fingerUpMs < FlingWindowMs))
            BeginBrowse();
        else
            _lastAppliedY = v.Y;
    }

    /// <summary>A fling keeps moving the list this long after the finger lifts.</summary>
    private const double FlingWindowMs = 2500;
    private bool _fingerDown;
    private double _fingerUpMs = double.NegativeInfinity;

    private void OnScrollPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _fingerDown = true;
        // A finger on the lines stops a compositor glide where it stands, so a drag carries
        // on from what is on screen (the scroller's offset is already the glide's target).
        if (IsGlidingOnCompositor) StopGlide();
        if (_lyrics is { IsBrowsing: true }) RestartBrowseTimer();   // a touch while reading holds the lines
    }

    private void OnScrollPointerReleased(object? sender, PointerEventArgs e)
    {
        if (!_fingerDown) return;
        _fingerDown = false;
        _fingerUpMs = LyricsPageViewModel.NowMs();
        // The wait counts from the lift (the last touch), not from the drag's last movement.
        if (_lyrics is { IsBrowsing: true }) RestartBrowseTimer();
    }

    /// <summary>The user took the lyrics: the glide stops, every line sharpens (the view model's
    /// browse ramp) and the page waits <see cref="BrowseResumeDelay"/> after the last touch
    /// before following the song again. Movement under the finger, or a fling within
    /// <see cref="FlingExtendWindow"/> of the lift, restarts the wait; a fling's later tail
    /// does not. Internal for tests.</summary>
    internal void BeginBrowse()
    {
        if (_lyrics == null) return;
        StopGlide();
        _lastAppliedY = LyricsScroll.Offset.Y;
        _lyrics.IsBrowsing = true;
        var touching = _fingerDown || LyricsPageViewModel.NowMs() - _fingerUpMs < FlingExtendWindow.TotalMilliseconds;
        if (touching || !_browseTimer.IsEnabled) RestartBrowseTimer();
    }

    private void RestartBrowseTimer()
    {
        _browseTimer.Stop();
        _browseTimer.Interval = BrowseResumeDelay;
        _browseTimer.Start();
    }

    internal void EndBrowse(bool scrollBack)
    {
        _browseTimer.Stop();
        if (_lyrics is not { IsBrowsing: true }) return;
        _lyrics.IsBrowsing = false;
        if (scrollBack && IsVisible) ScrollToLine(_lyrics.ScrollLineIndex, animate: true);
    }

    // ── Frames ──

    /// <summary>The page's frame loop is running (tests read it).</summary>
    internal bool IsFrameLoopRunning => _frameLoopRunning;

    /// <summary>How far ahead of a plain line's start the frame loop (and with it the keep-alive)
    /// wakes. Between lines nothing is drawn, and the S23's panel drops to 24 Hz after a moment
    /// without frames; its way back to 120 Hz takes 75-85 ms (RenderKeepAlive). Woken this early,
    /// on a position poll (every 250 ms), it is back at full rate before the line glides.</summary>
    internal const double WakeLeadMs = 600;

    // Not while the app is in the background (home, task switch, screen off): the frame loop and
    // the keep-alive kept the render loop ticking there for as long as the song played.
    private bool KeepsFrames => IsVisible && !_heldForMenu && _shell is not { IsSuspended: true }
                                && ((_gliding && !_onCompositor) || (_lyrics?.WantsFrames == true && LinesMoveSoon()));

    /// <summary>A word-timed song (or the dots) moves every frame; plain lines only around a change.</summary>
    private bool LinesMoveSoon() =>
        _lyrics is { } lyrics && (lyrics.AnimatesEveryFrame || lyrics.MsToNextChange(LyricsPageViewModel.NowMs()) <= WakeLeadMs);

    private void EnsureFrameLoop()
    {
        if (_frameLoopRunning || !KeepsFrames) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _frameLoopRunning = true;
        Services.FrameStallMonitor.SetState("lyric frame loop", true);
        KeepAlive.Run(true);
        top.RequestAnimationFrame(_onFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        Services.FrameStallMonitor.Count("lyric frames");
        var now = LyricsPageViewModel.NowMs();
        if (_gliding && !_onCompositor) StepGlide(now);
        if (_lyrics?.WantsFrames == true && IsVisible) _lyrics.OnFrame(now);

        if (KeepsFrames && TopLevel.GetTopLevel(this) is { } top)
        {
            KeepAlive.Run(true);   // a page shown this frame has its compositor visual only now
            top.RequestAnimationFrame(_onFrame);
        }
        else
        {
            _frameLoopRunning = false;
            Services.FrameStallMonitor.SetState("lyric frame loop", false);
            KeepAlive.Run(false);
        }
    }

    // ── Scroll ──

    private void ApplyOffset(double y)
    {
        _lastAppliedY = y;
        LyricsScroll.Offset = new Vector(0, y);
    }

    /// <summary>
    /// Puts line <paramref name="index"/> on the anchor, gliding with the desktop's line
    /// motion and cascade or jumping. The list is virtualised, so an off-screen line is
    /// realised first. Skipped while the user browses. Internal for tests.
    /// </summary>
    internal void ScrollToLine(int index, bool animate)
    {
        if (index < 0 || _lyrics == null || index >= _lyrics.Lines.Count || _lyrics.IsBrowsing) return;
        if (LyricsList.ContainerFromIndex(index) is not Control container)
        {
            LyricsList.ScrollIntoView(index);
            LyricsList.UpdateLayout();
            if (LyricsList.ContainerFromIndex(index) is not Control realised) return;
            container = realised;
        }

        if (container.TranslatePoint(new Point(0, 0), LyricsList) is not { } origin) return;
        // Coordinates relative to the list exclude its margin; the scroll extent includes it.
        var top = origin.Y + LyricsList.Margin.Top;
        var target = LyricsScrollAnchor.ComputeAnchorOffset(
            top, container.Bounds.Height, LyricsScroll.Viewport.Height, LyricsScroll.Extent.Height, AnchorRatio);
        // Beta (11-09): sideways on a 360 dp phone the viewport is ~230 dp, and centring a
        // wrapped sung line on the anchor put its first row under the top fade's solid wash.
        // The line's top stays below the fade (portrait's taller viewport seldom needs it).
        target = Math.Max(0, Math.Min(target, top - TopFade.Bounds.Height));

        // A glide in flight stops where it stands on screen; the next one sets off from there.
        StopGlide();
        if (!animate || Math.Abs(target - LyricsScroll.Offset.Y) < 2)
        {
            ApplyOffset(target);
            return;
        }

        _glideFrom = LyricsScroll.Offset.Y;
        _glideTo = target;
        _glideStartMs = LyricsPageViewModel.NowMs();
        var distance = Math.Abs(target - _glideFrom);
        // A line-to-line advance runs exactly LineMotion.DurationMs, landing with the line's
        // own scale transition; only seek-sized jumps stretch a little (the desktop's rule).
        _glideDurationMs = Math.Min(1050, Math.Max(LineMotion.DurationMs, distance * 0.85));
        BuildCascade(index, distance);
        _gliding = true;
        Services.FrameStallMonitor.SetState(Services.FrameStallMonitor.MotionState, true);
        if (StartCompositorGlide()) return;
        EnsureFrameLoop();
    }

    /// <summary>The lines below the active one that take part in the cascade: realised ones
    /// only (the list is virtualised; an unrealised line is off screen). Over a seek-sized
    /// jump there is no cascade, as on the desktop.</summary>
    private void BuildCascade(int activeIndex, double distance)
    {
        _cascade = null;
        _cascadeMaxDelayMs = 0;
        if (distance <= 8 || distance > LyricsScroll.Viewport.Height * 1.5) return;
        var rows = new List<(int Index, Control Control)>();
        foreach (var c in LyricsList.GetRealizedContainers())
        {
            var i = LyricsList.IndexFromContainer(c);
            if (i > activeIndex) rows.Add((i, c));
        }
        if (rows.Count == 0) return;
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        _cascade = rows.Select(r => (r.Control, Math.Min(r.Index - activeIndex, CascadeMaxLines) * LineMotion.CascadeDelayPerLineMs)).ToList();
        _cascadeMaxDelayMs = _cascade[^1].DelayMs;
    }

    /// <summary>The glide <paramref name="elapsedMs"/> in: the list's scroll offset (returned)
    /// and, into <paramref name="lags"/> (one per cascade line), each cascade line's lag behind
    /// it, the desktop's motion (LyricsView.AnimateScroll).</summary>
    private double GlideAt(double elapsedMs, double[]? lags)
    {
        var t = Math.Clamp(elapsedMs / _glideDurationMs, 0, 1);
        var eased = LineMotion.Ease(t);
        var delta = _glideTo - _glideFrom;
        if (lags != null && _cascade != null)
        {
            // Each cascade line is displaced by the gap between the base ease and its own
            // delayed ease, clamped to the line above's lag.
            var prevLag = 0.0;
            for (var i = 0; i < _cascade.Count; i++)
            {
                var tLine = Math.Clamp((elapsedMs - _cascade[i].DelayMs) / _glideDurationMs, 0.0, 1.0);
                prevLag = LineMotion.CascadeStep(delta * (eased - LineMotion.Ease(tLine)), prevLag, MaxCascadeLagStepPx);
                lags[i] = prevLag;
            }
        }
        return _glideFrom + delta * eased;
    }

    private void StepGlide(double nowMs)
    {
        var elapsed = nowMs - _glideStartMs;
        if (_cascade != null && (_lags == null || _lags.Length < _cascade.Count)) _lags = new double[_cascade.Count];
        ApplyOffset(GlideAt(elapsed, _cascade == null ? null : _lags));
        if (_cascade != null)
            for (var i = 0; i < _cascade.Count; i++) SetLag(_cascade[i].Control, _lags![i]);

        if (elapsed >= _glideDurationMs + _cascadeMaxDelayMs)
        {
            ApplyOffset(_glideTo);
            StopGlide();
        }
    }

    private double[]? _lags;

    private static void SetLag(Control control, double lag)
    {
        if (control.RenderTransform is TranslateTransform tt)
        {
            if (tt.Y != lag) tt.Y = lag;
        }
        else if (lag != 0)
        {
            control.RenderTransform = new TranslateTransform(0, lag);
        }
    }

    // ── The glide on the compositor ──

    /// <summary>
    /// S23 at 120 Hz (11-23): while the lines glided, the page wrote the scroll offset and each
    /// cascade line's lag on every frame: a layout pass, the scroller's events and a property
    /// change per line on every frame, which with the rest of the frame filled the phone's
    /// nursery every 25-30 s, its 80-100 ms GC pause now and then right in a glide. Now the
    /// offset jumps to the target once and the render thread plays the motion back: the list's
    /// composition Translation runs from where the lines stood to none on the line curve
    /// (<see cref="LineMotionEase"/>), and each cascade line's own Translation follows its lag,
    /// worked out from the same motion (<see cref="GlideAt"/>, see <see cref="LagStepMs"/>).
    /// The UI thread does nothing per frame. Taps hit what is on screen (Avalonia hit-tests the
    /// compositor's last frame). The lines the motion uncovers must already be realised: the
    /// panel keeps <see cref="RealisedBuffer"/> of a viewport beyond each edge (its CacheLength),
    /// and a longer jump glides frame by frame as before (<see cref="StepGlide"/>).
    /// </summary>
    private bool StartCompositorGlide()
    {
        var delta = _glideTo - _glideFrom;
        if (!CompositorGlide || Math.Abs(delta) > LyricsScroll.Viewport.Height * RealisedBuffer
            || ElementComposition.GetElementVisual(LyricsList) is not { } list) return false;
        _onCompositor = true;
        ApplyOffset(_glideTo);
        // The offset and the animation reach the render thread in the same batch: the jump
        // never shows.
        var glide = list.Compositor.CreateVector3DKeyFrameAnimation();
        glide.InsertKeyFrame(0f, new Vector3D(0, delta, 0), Linear);
        glide.InsertKeyFrame(1f, default, Ease);
        glide.Duration = TimeSpan.FromMilliseconds(_glideDurationMs);
        list.StartAnimation(TranslationProperty, glide);
        _translated.Add(list);

        var totalMs = _glideDurationMs + _cascadeMaxDelayMs;
        if (_cascade != null)
        {
            // The lags every LagStepMs, then per line only the key frames a straight run between
            // them cannot stand in for (LagKeyFrames).
            var rows = _cascade.Count;
            var steps = (int)Math.Ceiling(totalMs / LagStepMs);
            if (_lagCurve == null || _lagCurve.Length < (steps + 1) * rows) _lagCurve = new double[(steps + 1) * rows];
            if (_lags == null || _lags.Length < rows) _lags = new double[rows];
            for (var k = 0; k <= steps; k++)
            {
                GlideAt(Math.Min(k * LagStepMs, totalMs), _lags);
                Array.Copy(_lags, 0, _lagCurve, k * rows, rows);
            }
            for (var i = 0; i < rows; i++)
            {
                if (ElementComposition.GetElementVisual(_cascade[i].Control) is not { } row) continue;
                var lag = row.Compositor.CreateVector3DKeyFrameAnimation();
                lag.Duration = TimeSpan.FromMilliseconds(totalMs);
                LagKeyFrames(_lagCurve, steps, rows, i, LagTolerancePx, _keys);
                foreach (var k in _keys)
                    lag.InsertKeyFrame(k == steps ? 1f : (float)(k * LagStepMs / totalMs), new Vector3D(0, _lagCurve[k * rows + i], 0), Linear);
                row.StartAnimation(TranslationProperty, lag);
                _translated.Add(row);
            }
        }
        _glideDone.Stop();
        _glideDone.Interval = TimeSpan.FromMilliseconds(totalMs);
        _glideDone.Start();
        return true;
    }

    /// <summary>Off: every glide runs frame by frame (tests compare the two).</summary>
    internal static bool CompositorGlide { get; set; } = true;

    /// <summary>Fraction of a viewport the lyric panel keeps realised beyond each edge (its
    /// CacheLength in LyricsPage.axaml): the longest glide the compositor plays.</summary>
    internal const double RealisedBuffer = 0.5;

    /// <summary>The cascade lines' lag is worked out this often, and played back as straight runs
    /// between key frames that stay within <see cref="LagTolerancePx"/> of it (a third of a pixel
    /// of the true curve at worst, where a backward glide's clamp bends it). A fixed 30 Hz
    /// sampling was 2 px off a line's lag where its delayed start bends the curve sharply, and
    /// 120 Hz took ~1,300 key frames a glide (new objects each time).</summary>
    private const double LagStepMs = 2;
    internal const double LagTolerancePx = 0.25;

    // Scratch for the cascade's key frames, kept between glides.
    private double[]? _lagCurve;
    private readonly List<int> _keys = new();

    /// <summary>
    /// The samples (by index) of column <paramref name="column"/> of <paramref name="curve"/>
    /// (<paramref name="steps"/> + 1 rows of <paramref name="stride"/>) that straight runs must
    /// join for every sample in between to lie within <paramref name="tolerance"/>: the first,
    /// the last, and the fewest in between (greedy, each run as long as it fits). Internal for
    /// tests.
    /// </summary>
    internal static void LagKeyFrames(double[] curve, int steps, int stride, int column, double tolerance, List<int> keys)
    {
        keys.Clear();
        keys.Add(0);
        var from = 0;
        while (from < steps)
        {
            var to = from + 1;
            while (to < steps && Fits(from, to + 1)) to++;
            keys.Add(to);
            from = to;
        }

        bool Fits(int a, int b)
        {
            var va = curve[a * stride + column];
            var slope = (curve[b * stride + column] - va) / (b - a);
            for (var m = a + 1; m < b; m++)
                if (Math.Abs(va + slope * (m - a) - curve[m * stride + column]) > tolerance) return false;
            return true;
        }
    }

    private const string TranslationProperty = "Translation";
    private static readonly LinearEasing Linear = new();
    private static readonly LineMotionEase Ease = new();

    private bool _onCompositor;
    // Visuals whose Translation the compositor glide animates (the list and its cascade lines).
    private readonly List<CompositionVisual> _translated = new();

    /// <summary>A compositor glide is running (tests read it; one that has played out ends here
    /// if its timer has not run yet).</summary>
    internal bool IsGlidingOnCompositor
    {
        get
        {
            if (_gliding && _onCompositor && LyricsPageViewModel.NowMs() - _glideStartMs >= _glideDurationMs + _cascadeMaxDelayMs)
                OnCompositorGlideDone();
            return _gliding && _onCompositor;
        }
    }

    /// <summary>The compositor glide has played out: the translations rest at none.</summary>
    private void OnCompositorGlideDone()
    {
        _glideDone.Stop();
        if (!_onCompositor) return;
        _onCompositor = false;
        _translated.Clear();
        StopGlide();
    }

    /// <summary>Where the lines stand on screen as a scroll offset: the layout's offset, less
    /// what a compositor glide still displaces them by (on the glide's own clock; the render
    /// thread's starts with the next frame, at most a frame later).</summary>
    private double VisualOffset() =>
        _gliding && _onCompositor
            ? GlideAt(LyricsPageViewModel.NowMs() - _glideStartMs, null)
            : LyricsScroll.Offset.Y;

    /// <summary>Puts a composition Translation at <paramref name="y"/> at once, replacing an
    /// animation on it: a plain write ends it on the render thread (StopAnimation can miss one
    /// still on its way there, see RenderKeepAlive). The UI side keeps the last value written
    /// and drops an equal write, so then the same place is written a hair off in depth (2D
    /// drawing has no depth).</summary>
    private static void SetTranslation(CompositionVisual visual, double y)
    {
        var at = new Vector3D(0, y, 0);
        visual.Translation = visual.Translation == at ? new Vector3D(0, y, 1e-6) : at;
    }

    private void StopGlide()
    {
        if (_gliding && _onCompositor)
        {
            // Mid-glide: the offset takes where the lines are on screen and the translations
            // go, together (the cascade lines drop their lag, as the frame-by-frame glide did).
            var y = VisualOffset();
            _glideDone.Stop();
            _onCompositor = false;
            foreach (var visual in _translated) SetTranslation(visual, 0);
            _translated.Clear();
            ApplyOffset(y);
        }
        if (_gliding) Services.FrameStallMonitor.SetState(Services.FrameStallMonitor.MotionState, false);
        _gliding = false;
        if (_cascade == null) return;
        foreach (var (control, _) in _cascade) SetLag(control, 0);
        _cascade = null;
        _cascadeMaxDelayMs = 0;
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    /// before the page glides back to the sung line (Apple Music waits a few seconds).</summary>
    internal static TimeSpan BrowseResumeDelay { get; set; } = TimeSpan.FromSeconds(3.5);

    /// <summary>How long the transport stays up after a touch while the song plays.</summary>
    internal static TimeSpan ControlsHideDelay { get; set; } = TimeSpan.FromSeconds(4);

    private ShellViewModel? _shell;
    private LyricsPageViewModel? _lyrics;
    private bool _frameLoopRunning;

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

    public LyricsPage()
    {
        InitializeComponent();
        LyricsList.ContainerPrepared += (_, _) => Services.FrameStallMonitor.Count("lyric rows prepared");
        WashDither.Background = NowPlayingPage.Dither;
        LyricsScroll.SizeChanged += (_, _) => UpdateEdgePadding();
        LyricsScroll.PropertyChanged += OnScrollPropertyChanged;

        _browseTimer = new DispatcherTimer { Interval = BrowseResumeDelay };
        _browseTimer.Tick += (_, _) => EndBrowse(scrollBack: true);
        _controlsTimer = new DispatcherTimer { Interval = ControlsHideDelay };
        _controlsTimer.Tick += (_, _) => HideControlsIfPlaying();
        _controlsOffTimer = new DispatcherTimer { Interval = ControlsFadeOut };
        _controlsOffTimer.Tick += (_, _) => RemoveHiddenControls();

        // Any touch brings the transport back (and keeps it up a while), even one a line or
        // the scroller handles; a tap on a line also ends browsing, as the seek it makes
        // should be followed.
        AddHandler(PointerPressedEvent, (_, _) => ShowControls(), RoutingStrategies.Tunnel, handledEventsToo: true);
        LyricsList.AddHandler(Button.ClickEvent, (_, _) => EndBrowse(scrollBack: false), RoutingStrategies.Bubble);
        LyricsScroll.AddHandler(PointerPressedEvent, OnScrollPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        LyricsScroll.AddHandler(PointerReleasedEvent, OnScrollPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Backdrop.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty) UpdateFades(); };
        TopFade.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateFades(); };
        BottomFade.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) UpdateFades(); };
        SizeChanged += (_, e) =>
        {
            ApplyOrientation(e.NewSize);
            UpdateFades();
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
        if (_shell != null) _shell.Player.PropertyChanged -= OnPlayerChanged;
        _shell = DataContext as ShellViewModel;
        _lyrics = _shell?.Lyrics;
        if (_lyrics != null)
        {
            _lyrics.PropertyChanged += OnLyricsChanged;
            _lyrics.Lines.CollectionChanged += OnLinesChanged;
        }
        if (_shell != null) _shell.Player.PropertyChanged += OnPlayerChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (!IsVisible)
        {
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
        else if (e.PropertyName == nameof(LyricsPageViewModel.ScrollLineIndex) && IsVisible)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            ScrollToLine(_lyrics!.ScrollLineIndex, animate: true);
            Services.FrameStallMonitor.AddTime("lyric scroll start", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        else if (e.PropertyName == nameof(LyricsPageViewModel.HasLyrics) && IsVisible)
            ShowControls();   // (re)starts the hide timer once there is something to read
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.IsPlaying) && IsVisible) ShowControls();
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
        var landscape = size.Width > size.Height;
        var safe = _shell?.SafeArea ?? default;
        var side = landscape ? safe.Left + 24 + Math.Max(0, size.Width - safe.Left - safe.Right - 48) * 5 / 11 : 0;
        if (_landscape == landscape && side == _sideWidth) return;
        _landscape = landscape;
        _sideWidth = side;
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
        var art = Backdrop.IsVisible ? Backdrop.Source as IImageBrushSource : null;
        foreach (var band in new[] { TopFade, BottomFade })
        {
            if (band.TranslatePoint(new Point(0, 0), this) is not { } at) continue;
            band.Update(art, (Background as ISolidColorBrush)?.Color ?? Colors.Black, Shade, new Rect(at, band.Bounds.Size), page);
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

    private void OnScrollPointerPressed(object? sender, PointerPressedEventArgs e) => _fingerDown = true;

    private void OnScrollPointerReleased(object? sender, PointerEventArgs e)
    {
        if (!_fingerDown) return;
        _fingerDown = false;
        _fingerUpMs = LyricsPageViewModel.NowMs();
    }

    /// <summary>The user took the lyrics: the glide stops, every line sharpens (the view model's
    /// browse ramp) and the page waits <see cref="BrowseResumeDelay"/> after the last movement
    /// before following the song again. Internal for tests.</summary>
    internal void BeginBrowse()
    {
        if (_lyrics == null) return;
        StopGlide();
        _lastAppliedY = LyricsScroll.Offset.Y;
        _lyrics.IsBrowsing = true;
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

    private bool KeepsFrames => IsVisible && (_gliding || _lyrics?.WantsFrames == true);

    private void EnsureFrameLoop()
    {
        if (_frameLoopRunning || !KeepsFrames) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _frameLoopRunning = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        var now = LyricsPageViewModel.NowMs();
        if (_gliding) StepGlide(now);
        if (_lyrics?.WantsFrames == true && IsVisible) _lyrics.OnFrame(now);

        if (KeepsFrames && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(OnFrame);
        else
            _frameLoopRunning = false;
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
        var target = LyricsScrollAnchor.ComputeAnchorOffset(
            origin.Y + LyricsList.Margin.Top, container.Bounds.Height,
            LyricsScroll.Viewport.Height, LyricsScroll.Extent.Height, AnchorRatio);

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

    private void StepGlide(double nowMs)
    {
        var elapsed = nowMs - _glideStartMs;
        var t = Math.Clamp(elapsed / _glideDurationMs, 0, 1);
        var eased = LineMotion.Ease(t);
        var delta = _glideTo - _glideFrom;
        ApplyOffset(_glideFrom + delta * eased);

        if (_cascade != null)
        {
            // Each cascade line is displaced by the gap between the base ease and its own
            // delayed ease, clamped to the line above's lag (LyricsView.AnimateScroll).
            var prevLag = 0.0;
            foreach (var (control, delayMs) in _cascade)
            {
                var tLine = Math.Clamp((elapsed - delayMs) / _glideDurationMs, 0.0, 1.0);
                var lag = LineMotion.CascadeStep(delta * (eased - LineMotion.Ease(tLine)), prevLag, MaxCascadeLagStepPx);
                prevLag = lag;
                SetLag(control, lag);
            }
        }

        if (t >= 1 && elapsed >= _glideDurationMs + _cascadeMaxDelayMs)
        {
            ApplyOffset(_glideTo);
            StopGlide();
        }
    }

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

    private void StopGlide()
    {
        _gliding = false;
        if (_cascade == null) return;
        foreach (var (control, _) in _cascade) SetLag(control, 0);
        _cascade = null;
        _cascadeMaxDelayMs = 0;
    }
}

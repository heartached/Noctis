using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class ShellView : UserControl
{
    private IInsetsManager? _insets;

    public ShellView()
    {
        InitializeComponent();
        // The TopLevel pads its main view by the safe area by default; the shell pads each
        // layer itself (SafeArea) so the overlays' backgrounds run under the system bars,
        // and both together doubled the gap (~49 dp extra on top, 24 dp at the bottom).
        TopLevel.SetAutoSafeAreaPadding(this, false);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TabContent.AddHandler(ScrollViewer.ScrollChangedEvent, OnContentScrollChanged);
        ChromeHost.SizeChanged += (_, _) => ApplyChromeLayout();
        // The tab bar stays up under the Queue sheet (the owner's mockup): the sheet slides it
        // with itself, and the bar sits over the overlays while the page is on screen.
        QueueSheet.TabBar = BottomChrome;
        QueueSheet.PropertyChanged += OnQueueSheetPropertyChanged;
        PageHost.Root = RootLayer;
        // A swipe down on a sheet drops it (SheetDrag below). Tunnel, handled or not: the press
        // lands on the sheet's buttons and cover too.
        SheetHost.AddHandler(PointerPressedEvent, OnSheetPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        PlayerLayer.AddHandler(PointerPressedEvent, OnSheetPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnSheetDragMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnSheetDragReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnSheetDragCaptureLost, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += OnDataContextChanged;
        // Swipe the mini player sideways for the next or previous song.
        MiniSwipeGesture = new TrackSwipeGesture(MiniSwipe, MiniRow, MiniGhost, MiniGhost, MiniBar, gap: 12,
            () => _vm?.Player, () => _vm is { IsNowPlayingOpen: false });
    }

    /// <summary>The mini player's sideways swipe (internal for tests).</summary>
    internal TrackSwipeGesture MiniSwipeGesture { get; }

    /// <summary>Raised while the Queue page shows, its slide down included; the long-press sheet
    /// stays above it (ZIndex 2 in the XAML). Under the Queue the bar stands at full size though
    /// Now Playing (a sheet) is up; it arrives and leaves off screen, so it changes size unseen.</summary>
    private void OnQueueSheetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        ChromeScope.ZIndex = QueueSheet.IsVisible ? 1 : 0;
        ApplyChromeTint();
        Motion.Stop(ChromeLayer);
        ChromeLayer.RenderTransform = ChromeScale(_covered);
    }

    /// <summary>
    /// Avalonia.Android turns the Back key into an Escape KeyDown on the focused control (and
    /// the activity's BackRequested follows for the same press, handled or not: TryHandleBackKey
    /// marks the press so the shell skips that echo, S23 2026-10-05). A slider keeps focus
    /// after a touch and marks Escape handled, so Back did nothing on Now Playing or Settings
    /// once a slider had been dragged (device run B12). Taking Escape here, in the tunnel
    /// phase, runs the shell's Back before any focused control sees the key; at the Home
    /// root it stays unhandled so the activity still falls through to finish.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is ShellViewModel vm && vm.TryHandleBackKey()) e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (_insets == null) return;   // headless tests: no system bars
        // Draw under the status and navigation bars (Android 15 enforces this for SDK 35+
        // anyway) and pad by the reported insets, so the layout is the same on every API level.
        _insets.DisplayEdgeToEdgePreference = true;
        _insets.SafeAreaChanged += OnSafeAreaChanged;
        ApplySafeArea(_insets.SafeAreaPadding);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets != null) _insets.SafeAreaChanged -= OnSafeAreaChanged;
        _insets = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Avalonia.Android 12.1.2 raises SafeAreaChanged once at startup, before the TopLevel
    /// knows its scaling, with the insets in physical pixels (0,128,0,63 on a 420 dpi phone
    /// instead of 0,48.8,0,24), and raises nothing when the scaling lands. The manager's
    /// SafeAreaPadding reads right once the view is sized, so re-read it on every resize
    /// (first layout, rotation); without this the tab content sat ~130 dp too low.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_insets != null) ApplySafeArea(_insets.SafeAreaPadding);
    }

    /// <summary>Re-reads the manager rather than trusting <see cref="SafeAreaChangedArgs.SafeAreaPadding"/>,
    /// which can carry the physical-pixel insets described above; one source for both paths.</summary>
    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        if (_insets != null) ApplySafeArea(_insets.SafeAreaPadding);
    }

    /// <summary>Internal for tests, which have no insets manager.</summary>
    internal void ApplySafeArea(Thickness padding)
    {
        if (DataContext is ShellViewModel vm) vm.SafeArea = padding;
    }

    // ---- Glass tab bar -------------------------------------------------------------------
    // Expanded: the mini player capsule over a full-width tab capsule, 8 dp apart (Apple Music,
    // iOS 26). Folded (scrolled down): one row of a round current-tab button, the mini player
    // and a round Search button, all the mini player's height. Sizes are set here and the
    // transitions attached once below animate every change between them.

    private const double TabHeight = 62, MiniHeight = 52, Bubble = 52, Gap = 8, FoldGap = 12;
    private const double ChromeBottomMargin = 8;

    /// <summary>The selected tab's inner capsule: a little wider than its tab (Apple's), kept
    /// this far inside the pill's ends.</summary>
    private const double PillInset = 4, PillOverhang = 2;

    /// <summary>The tabs across the expanded bar: Home, Favorites, Playlists, Library, Search.</summary>
    internal const int TabCount = 5;
    private static readonly TimeSpan Morph = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(200);

    private ShellViewModel? _vm;
    private bool _transitionsOn;
    private int _pillIndex = -1;
    private DispatcherTimer? _settleTimer;

    /// <summary>The droplet's slide between tabs, how long it takes to lift (and to settle),
    /// and how long it stays up: it lands as the slide's spring comes to rest.</summary>
    private static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan LiftTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LiftHold = TimeSpan.FromMilliseconds(170);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnShellPropertyChanged;
        if (_vm != null) _vm.Player.PropertyChanged -= OnPlayerPropertyChanged;
        _vm = DataContext as ShellViewModel;
        if (_vm != null) _vm.PropertyChanged += OnShellPropertyChanged;
        if (_vm != null) _vm.Player.PropertyChanged += OnPlayerPropertyChanged;
        ApplyChromeLayout();
        SyncPages(animate: false);
        ShowPlayer(animate: false);
        ShowLyrics(animate: false);
    }

    /// <summary>How long the player's banner (NowPlayingViewModel.Notice) stays up.</summary>
    internal static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _noticeTimer;

    /// <summary>Each new notice gets its full time on screen, then the banner goes.</summary>
    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NowPlayingViewModel.Notice) || _vm?.Player is not { HasNotice: true }) return;
        _noticeTimer ??= new DispatcherTimer(NoticeTime, DispatcherPriority.Normal, (_, _) =>
        {
            _noticeTimer?.Stop();
            _vm?.Player.DismissNotice();
        });
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Unfolded as the Queue opens: the bar rises with the sheet already laid out rather
        // than morphing on the way up (ApplyChromeLayout re-attaches the transitions).
        if (e.PropertyName == nameof(ShellViewModel.IsTabBarCollapsed) && _vm?.IsQueueOpen == true) DetachTransitions();
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            TrackPageTint();
            SyncPages(animate: _vm?.LastPageMove != PageMove.Reset);
        }
        if (e.PropertyName == nameof(ShellViewModel.IsNowPlayingOpen)) ShowPlayer(animate: true);
        if (e.PropertyName == nameof(ShellViewModel.IsLyricsOpen)) ShowLyrics(animate: true);
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab)) FadeInTab();
        if (e.PropertyName is nameof(ShellViewModel.IsTabBarCollapsed) or nameof(ShellViewModel.IsMiniBarVisible)
            or nameof(ShellViewModel.SelectedTab) or nameof(ShellViewModel.SafeArea) or nameof(ShellViewModel.IsSelecting))
            ApplyChromeLayout();
    }

    /// <summary>A page's vertical scroll, from any list in the tab content (horizontal shelves
    /// report no vertical change and are skipped by the view model).</summary>
    private void OnContentScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm == null || e.Source is not ScrollViewer viewer || e.OffsetDelta.Y == 0) return;
        _vm.ReportContentScroll(viewer.Offset.Y, e.OffsetDelta.Y);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyChromeLayout();
    }

    private void AttachTransitions()
    {
        _transitionsOn = true;
        // An iOS-style settle: fast out of the gate, long soft landing.
        var ease = new CubicBezierEase(0.32, 0.72, 0, 1);
        TabCapsule.Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = Morph, Easing = ease },
            new DoubleTransition { Property = HeightProperty, Duration = Morph, Easing = ease },
        };
        TabRow.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Quick, Easing = ease } };
        CollapsedTabButton.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Morph, Easing = ease } };
        SearchBubble.Transitions = new Transitions
        {
            new DoubleTransition { Property = GlassPanel.FadeProperty, Duration = Morph, Easing = ease },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Morph, Easing = ease },
        };
        SearchBubbleContent.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Morph, Easing = ease } };
        MiniBar.Transitions = new Transitions { new ThicknessTransition { Property = MarginProperty, Duration = Morph, Easing = ease } };
        // The droplet slides with a little spring (it overshoots the tab by a few percent and
        // settles) while it lifts; see LiftSelectionPill.
        SelectionPillHost.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Slide, Easing = new CubicBezierEase(0.3, 1.18, 0.45, 1) },
        };
        SelectionPill.Transitions = new Transitions
        {
            new DoubleTransition { Property = GlassDroplet.LiftProperty, Duration = LiftTime, Easing = new CubicBezierEase(0.25, 0.1, 0.25, 1) },
        };
    }

    private void DetachTransitions()
    {
        _transitionsOn = false;
        TabCapsule.Transitions = null;
        TabRow.Transitions = null;
        CollapsedTabButton.Transitions = null;
        SearchBubble.Transitions = null;
        SearchBubbleContent.Transitions = null;
        MiniBar.Transitions = null;
        SelectionPillHost.Transitions = null;
        SelectionPill.Transitions = null;
        SelectionPill.Lift = 0;
    }

    /// <summary>
    /// A tab tap: the droplet lifts (its transition eases Lift up), and once it has travelled
    /// most of the way it settles back, so it is lifted over the slide and a resting grey
    /// pill again on the new tab. Two property sets per tap, never one per frame; a tap
    /// mid-flight re-lifts from wherever the droplet is.
    /// </summary>
    private void LiftSelectionPill()
    {
        SelectionPill.Lift = 1;
        _settleTimer ??= new DispatcherTimer(LiftTime + LiftHold, DispatcherPriority.Normal, (_, _) =>
        {
            _settleTimer!.Stop();
            SelectionPill.Lift = 0;
        });
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    internal static double SelectionPillWidth(double barWidth) => barWidth / TabCount + 2 * PillOverhang;

    /// <summary>Where the selected tab's capsule sits: centred on its tab, but never closer than
    /// <see cref="PillInset"/> to the bar's rounded ends.</summary>
    internal static double SelectionPillX(double barWidth, int index)
    {
        var x = index * (barWidth / TabCount) - PillOverhang;
        return Math.Clamp(x, PillInset, Math.Max(PillInset, barWidth - SelectionPillWidth(barWidth) - PillInset));
    }

    /// <summary>A tab's column in the bar (TabBar in the XAML).</summary>
    internal static int TabIndex(MobileTab tab) => tab switch
    {
        MobileTab.Home => 0,
        MobileTab.Favorites => 1,
        MobileTab.Playlists => 2,
        MobileTab.Library => 3,
        _ => 4,
    };

    /// <summary>How tall the expanded chrome stands (mini player + tabs, or tabs alone).</summary>
    internal static double ExpandedChromeHeight(bool miniVisible) => miniVisible ? TabHeight + Gap + MiniHeight : TabHeight;

    /// <summary>Lays out the bar for the view model's current state. Internal for tests.</summary>
    internal void ApplyChromeLayout()
    {
        if (_vm is not { } vm) return;
        var folded = vm.IsTabBarCollapsed;
        // Select mode's action bar takes the mini player's capsule, so the chrome stands as tall.
        var mini = vm.IsMiniBarVisible || vm.IsSelecting;
        var width = ChromeHost.Bounds.Width;

        var expandedHeight = ExpandedChromeHeight(mini);
        ChromeHost.Height = folded ? Bubble : expandedHeight;
        // Lists end this far below their last row so it can scroll clear of the bar; the
        // expanded height, so the end of a list never hides when the bar unfolds over it.
        Resources["ShellBottomInset"] = new Thickness(0, 0, 0, expandedHeight + ChromeBottomMargin + vm.SafeArea.Bottom + 16);
        BottomScrim.Height = expandedHeight + ChromeBottomMargin + vm.SafeArea.Bottom + 36;
        PageScrim.Height = BottomScrim.Height;
        // Under the Queue only the tab capsule stands: the Queue opens from Now Playing, which
        // hides the mini player.
        QueueSheet.TabBarInset = TabHeight + ChromeBottomMargin + vm.SafeArea.Bottom;

        if (width <= 0) return;

        TabCapsule.Width = folded ? Bubble : width;
        TabCapsule.Height = folded ? Bubble : TabHeight;
        TabRow.Width = width;
        TabRow.Opacity = folded ? 0 : 1;
        TabRow.IsHitTestVisible = !folded;
        CollapsedTabButton.Width = Bubble;
        CollapsedTabButton.Height = Bubble;
        CollapsedTabButton.Opacity = folded ? 1 : 0;
        CollapsedTabButton.IsHitTestVisible = folded;

        // Folded on the Search tab the current-tab circle is already Search: the round Search
        // button beside it was a second magnifier. It stands aside and the mini player takes its room.
        var searchBubble = folded && vm.SelectedTab != MobileTab.Search;
        SearchBubble.Width = Bubble;
        SearchBubble.Height = Bubble;
        SearchBubble.Fade = searchBubble ? 1 : 0;
        SearchBubble.RenderTransform = TransformOperations.Parse(searchBubble ? "scale(1)" : "scale(0.6)");
        SearchBubbleContent.Opacity = searchBubble ? 1 : 0;
        SearchBubble.IsHitTestVisible = searchBubble;

        var miniMargin = folded ? new Thickness(Bubble + FoldGap, 0, searchBubble ? Bubble + FoldGap : 0, 0) : new Thickness(0, 0, 0, TabHeight + Gap);
        MiniBar.Height = MiniHeight;
        MiniBar.Margin = miniMargin;
        MiniNextButton.IsVisible = !folded;
        SelectionBar.Height = MiniHeight;
        SelectionBar.Margin = miniMargin;

        var index = TabIndex(vm.SelectedTab);
        var pillWidth = SelectionPillWidth(width);
        SelectionPill.Width = pillWidth;
        SelectionPill.Height = TabHeight - 2 * PillInset;
        var pillX = TransformOperations.Parse($"translateX({SelectionPillX(width, index).ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
        if (_transitionsOn && index != _pillIndex && _pillIndex >= 0) LiftSelectionPill();
        _pillIndex = index;
        SelectionPillHost.RenderTransform = pillX;

        // Once the bar has been laid out at its real width, so it appears in place rather
        // than animating in from zero.
        if (!_transitionsOn && IsLoaded) AttachTransitions();
    }

    // ---- Pages, sheets and the player: the motion system (Motion) --------------------------

    /// <summary>
    /// Puts the page stack on screen: the pages up to the first sheet in PageHost, over the tab
    /// roots; the sheet and the pages pushed inside it in SheetHost, over the tab bar. A page
    /// pushed slides in, a popped one slides out, a sheet rises or drops; a reset (a tab tap)
    /// jumps, as does the first layout.
    /// </summary>
    private void SyncPages(bool animate)
    {
        if (_vm is not { } vm) return;
        var pages = vm.Pages;
        var sheetAt = -1;
        for (var i = 0; i < pages.Count && sheetAt < 0; i++)
            if (pages[i].IsSheet) sheetAt = i;
        var tabTop = sheetAt < 0 ? pages.LastOrDefault() : sheetAt > 0 ? pages[sheetAt - 1] : null;
        var sheetTop = sheetAt < 0 ? null : pages[^1];
        Move(PageHost, tabTop, sheet: false);
        Move(SheetHost, sheetTop, sheet: true);
        ApplyBackdrop(animate);

        void Move(PageStack stack, MobilePage? top, bool sheet)
        {
            if (top == stack.Top) return;
            if (!animate) stack.Reset(top);
            else if (sheet && stack.Top == null) stack.Present(top!);
            else if (sheet && top == null) stack.Dismiss();
            // The old top still in the stack: the new one went on over it.
            else if (top != null && (stack.Top == null || pages.Contains(stack.Top))) stack.Push(top);
            else stack.Pop(top);
        }
    }

    private bool _covered;

    private static ITransform? AppScale(bool covered) => covered ? Motion.Scale(Motion.BehindScale) : null;

    /// <summary>The chrome steps back with the app, except under the Queue, which keeps it at full size.</summary>
    private ITransform? ChromeScale(bool covered) => AppScale(covered && !QueueSheet.IsVisible);

    /// <summary>
    /// The app behind a sheet (Settings, Now Playing) steps back a little and dims, as iOS does,
    /// and the dim takes the taps meant for it (a second tap on the profile circle while the
    /// sheet rises); both come back as the sheet goes.
    /// </summary>
    private void ApplyBackdrop(bool animate)
    {
        if (_vm is not { } vm) return;
        var covered = vm.IsNowPlayingOpen || vm.Pages.Any(p => p.IsSheet);
        if (covered == _covered) return;
        _covered = covered;
        SheetDim.IsHitTestVisible = covered;
        if (covered) SheetDim.IsVisible = true;
        if (!animate)
        {
            foreach (var c in new Control[] { AppLayer, ChromeLayer, SheetDim }) Motion.Stop(c);
            AppLayer.RenderTransform = AppScale(covered);
            ChromeLayer.RenderTransform = ChromeScale(covered);
            SheetDim.Opacity = covered ? Motion.BehindDim : 0;
            SheetDim.IsVisible = covered;
            return;
        }
        // Back at full size the layers drop their transforms, as if never moved.
        var still = Motion.Scale(1);
        Motion.To(AppLayer, Motion.Sheet, AppScale(covered) ?? still,
            from: Motion.IsRunning(AppLayer) ? null : AppLayer.RenderTransform ?? still,
            done: () => AppLayer.RenderTransform = AppScale(_covered));
        Motion.To(ChromeLayer, Motion.Sheet, ChromeScale(covered) ?? still,
            from: Motion.IsRunning(ChromeLayer) ? null : ChromeLayer.RenderTransform ?? still,
            done: () => ChromeLayer.RenderTransform = ChromeScale(_covered));
        Motion.To(SheetDim, Motion.Sheet, opacity: covered ? Motion.BehindDim : 0, done: () => SheetDim.IsVisible = _covered);
    }

    private bool _playerShown;

    /// <summary>Now Playing rises from the bottom edge as a sheet and drops back on close (the
    /// grabber, Back, a page opened from it), Lyrics with it when it is over the player. It
    /// takes touches once it has landed, none on the way down.</summary>
    private void ShowPlayer(bool animate)
    {
        var open = _vm?.IsNowPlayingOpen == true;
        if (open == _playerShown) return;
        _playerShown = open;
        var down = Motion.TranslateY(Bounds.Height > 0 ? Bounds.Height : 915);
        NowPlaying.IsHitTestVisible = false;
        if (open) NowPlaying.IsVisible = true;
        void Land()
        {
            NowPlaying.IsVisible = _playerShown && !_lyricsCovers;
            NowPlaying.IsHitTestVisible = true;
            PlayerLayer.RenderTransform = null;
        }
        if (!animate)
        {
            Motion.Stop(PlayerLayer);
            Land();
        }
        else if (open)
            Motion.To(PlayerLayer, Motion.Sheet, Motion.TranslateY(0), from: Motion.IsRunning(PlayerLayer) ? null : down, enter: true, done: Land);
        else
            Motion.To(PlayerLayer, Motion.Sheet, down, done: Land);
        ApplyBackdrop(animate);
    }

    /// <summary>Lyrics fades in over the player and out again. Once it has faded in it covers
    /// Now Playing completely (an opaque page), so Now Playing leaves the tree until the fade
    /// back out starts: left visible under the lyrics, its elapsed and remaining labels laid
    /// out the page once a second and its seek bar repainted on every position tick, in the
    /// middle of the word sweep (a periodic hitch on a mid-range phone).</summary>
    private void ShowLyrics(bool animate)
    {
        var open = _vm?.IsLyricsOpen == true;
        LyricsSheet.IsHitTestVisible = open;
        if (open) LyricsSheet.IsVisible = true;
        if (!open)
        {
            _lyricsCovers = false;
            if (_playerShown) NowPlaying.IsVisible = true;   // back before the fade starts
        }
        if (!animate)
        {
            Motion.Stop(LyricsSheet);
            LyricsSheet.Opacity = 1;
            LyricsSheet.IsVisible = open;
            _lyricsCovers = open;
            NowPlaying.IsVisible = _playerShown && !_lyricsCovers;
            return;
        }
        Motion.To(LyricsSheet, Motion.Fade, opacity: open ? 1 : 0,
            fromOpacity: open && !Motion.IsRunning(LyricsSheet) ? 0 : null, done: () =>
            {
                var stillOpen = _vm?.IsLyricsOpen == true;
                LyricsSheet.IsVisible = stillOpen;
                LyricsSheet.Opacity = 1;
                _lyricsCovers = stillOpen;
                NowPlaying.IsVisible = _playerShown && !_lyricsCovers;
            });
    }

    private bool _lyricsCovers;

    /// <summary>A tab switch: the new tab fades in, quickly, with no slide.</summary>
    private void FadeInTab() =>
        Motion.To(TabContent, Motion.Fade, opacity: 1, fromOpacity: Motion.IsRunning(TabContent) ? null : 0);

    // ---- Swipe down to dismiss a sheet -----------------------------------------------------
    // Settings by its header, Now Playing by its upper half (grabber and cover): the sheet
    // follows the finger, the app behind comes forward as it goes, and on release it either
    // drops (pulled a quarter of the way, or flicked) or settles back up. Only the header and
    // the cover: lower down the lists, sliders and buttons keep their own drags. No edge swipe
    // back: Android's own Back gesture owns the screen edges and runs the same pop.

    /// <summary>How far the finger must travel before the drag is the sheet's, the header's
    /// height on a sheet, the share of the screen that drops it, and the flick (dp/ms) that does.</summary>
    private const double DragSlop = 10, SheetHeaderZone = 72, DismissShare = 0.25, DismissSpeed = 0.5;

    private Control? _dragSheet;
    private Point _dragStart;
    private bool _dragging;
    private double _dragLastY, _dragVelocity;
    private ulong _dragLastTime;

    private void OnSheetPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragSheet = null;
        _dragging = false;
        if (_vm is not { } vm || sender is not Control sheet || Motion.IsRunning(sheet)) return;
        var at = e.GetPosition(sheet);
        var ok = sheet == SheetHost
            ? SheetHost.Top != null && at.Y <= SheetHeaderZone
            : vm is { IsNowPlayingOpen: true, IsLyricsOpen: false, IsQueueOpen: false, IsMenuOpen: false }
              && at.Y <= sheet.Bounds.Height / 2
              && (e.Source as Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.RangeBase>(includeSelf: true) == null;
        if (!ok) return;
        _dragSheet = sheet;
        _dragStart = e.GetPosition(this);
        _dragLastY = _dragStart.Y;
        _dragLastTime = e.Timestamp;
        _dragVelocity = 0;
    }

    private void OnSheetDragMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSheet is not { } sheet) return;
        var p = e.GetPosition(this);
        var dy = p.Y - _dragStart.Y;
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) > DragSlop || dy < -DragSlop)
            {
                _dragSheet = null;   // sideways or up: not ours
                return;
            }
            if (dy <= DragSlop) return;
            _dragging = true;
            e.Pointer.Capture(sheet);   // the button under the finger lets go and does not click
            foreach (var c in new Control[] { sheet, AppLayer, ChromeLayer, SheetDim }) Motion.Stop(c);
        }
        if (e.Timestamp > _dragLastTime)
        {
            _dragVelocity = (p.Y - _dragLastY) / (e.Timestamp - _dragLastTime);
            _dragLastY = p.Y;
            _dragLastTime = e.Timestamp;
        }
        FollowDrag(sheet, Math.Max(0, dy));
        e.Handled = true;
    }

    /// <summary>The sheet at <paramref name="offset"/> below its place, the app behind as far
    /// forward as the sheet is gone. Direct sets per move, no transitions (they would stall).</summary>
    private void FollowDrag(Control sheet, double offset)
    {
        var height = Bounds.Height > 0 ? Bounds.Height : 915;
        var gone = Math.Clamp(offset / height, 0, 1);
        sheet.RenderTransform = Motion.TranslateY(offset);
        var scale = Motion.Scale(Motion.BehindScale + (1 - Motion.BehindScale) * gone);
        AppLayer.RenderTransform = scale;
        if (!QueueSheet.IsVisible) ChromeLayer.RenderTransform = scale;
        SheetDim.Opacity = Motion.BehindDim * (1 - gone);
    }

    private void OnSheetDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging || _dragSheet is not { } sheet)
        {
            _dragSheet = null;
            return;
        }
        var dy = e.GetPosition(this).Y - _dragStart.Y;
        EndDrag(sheet, drop: dy > (Bounds.Height > 0 ? Bounds.Height : 915) * DismissShare || _dragVelocity > DismissSpeed);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnSheetDragCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Taken away mid-drag (the system shade, a second finger's gesture): settle back up.
        if (_dragging && _dragSheet is { } sheet) EndDrag(sheet, drop: false);
    }

    private void EndDrag(Control sheet, bool drop)
    {
        _dragging = false;
        _dragSheet = null;
        if (drop && _vm is { } vm)
        {
            // The view model closes it; the close runs on from where the finger left the sheet.
            if (sheet == SheetHost) vm.DismissSheet();
            else vm.CloseNowPlayingCommand.Execute(null);
            return;
        }
        Motion.To(sheet, Motion.Sheet, Motion.TranslateY(0));
        Motion.To(AppLayer, Motion.Sheet, AppScale(_covered) ?? Motion.Scale(1), done: () => AppLayer.RenderTransform = AppScale(_covered));
        Motion.To(ChromeLayer, Motion.Sheet, ChromeScale(_covered) ?? Motion.Scale(1), done: () => ChromeLayer.RenderTransform = ChromeScale(_covered));
        Motion.To(SheetDim, Motion.Sheet, opacity: _covered ? Motion.BehindDim : 0);
    }

    // ---- Page-coloured bottom fade -------------------------------------------------------

    private PageTint? _scrimTint;

    /// <summary>Follows the top page's cover tint (an <see cref="ITintedPage"/>), which can land
    /// after the page opens: the extraction runs off the UI thread.</summary>
    private void TrackPageTint()
    {
        var tint = (_vm?.CurrentPage as ITintedPage)?.Tint;
        if (!ReferenceEquals(tint, _scrimTint))
        {
            if (_scrimTint != null) _scrimTint.PropertyChanged -= OnPageTintChanged;
            _scrimTint = tint;
            if (tint != null) tint.PropertyChanged += OnPageTintChanged;
        }
        ApplyPageScrim();
    }

    private void OnPageTintChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageTint.TintColor)) ApplyPageScrim();
    }

    /// <summary>The theme's fade under the bar is a white haze in Light and a dark one in Dark;
    /// over a page in its cover's colour it showed as a band, so that page fades into its own colour.</summary>
    private void ApplyPageScrim()
    {
        if (_scrimTint?.TintColor is { } c)
        {
            PageScrim.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0),
                    new GradientStop(Color.FromArgb(0xD0, c.R, c.G, c.B), 1),
                },
            };
            PageScrim.IsVisible = true;
            BottomScrim.IsVisible = false;
        }
        else
        {
            PageScrim.IsVisible = false;
            BottomScrim.IsVisible = true;
        }
        ApplyChromeTint();
    }

    /// <summary>The page colour the chrome's glass wears now, or null for the theme's glass.</summary>
    internal Color? ChromeTint { get; private set; }

    /// <summary>
    /// Over a cover-coloured page the tab bar, the mini player and the folded bubbles are glass in
    /// that colour (TintedGlass), their labels in the text the colour takes and the scope in the
    /// matching theme (Fluent's button glyphs flip with it); the theme's own glass elsewhere, and
    /// while the Queue sheet, which follows the app theme, holds the bar over it.
    /// </summary>
    private void ApplyChromeTint()
    {
        Color? tint = QueueSheet.IsVisible ? null : _scrimTint?.TintColor;
        ChromeTint = tint;
        TintedGlass.Clear(ChromeScope.Resources);
        if (tint is { } c)
        {
            TintedGlass.Apply(ChromeScope.Resources, c);
            var accent = this.TryFindResource("AccentColorBrush", ActualThemeVariant, out var a) && a is ISolidColorBrush ab ? ab.Color
                : Color.Parse(Noctis.Mobile.Services.MobileTheme.DefaultAccent);
            TintedGlass.ApplyText(ChromeScope.Resources, c, accent);
            ChromeScope.RequestedThemeVariant = PageTint.PrefersDarkText(TintedGlass.Face(c))
                ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        }
        else ChromeScope.ClearValue(ThemeVariantScope.RequestedThemeVariantProperty);
    }
}

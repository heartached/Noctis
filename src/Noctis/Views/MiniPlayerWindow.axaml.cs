using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Resizable always-on-top "liquid glass" mini player. The layout morphs between
/// five forms based on the window size (see <see cref="MiniPlayerViewModel.ComputeForm"/>):
/// tiny icon, horizontal bar, vertical card, tall large-icon, and a split lyrics view.
/// The "…" menu opens bottom-sheet layers (library search / queue / volume) over the
/// card. Opened/closed by the mini player button in the bottom player bar; closing it
/// restores the main window (handled by <see cref="MainWindow.ToggleMiniPlayer"/>).
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const int CloseAnimationMs = 170;
    private bool _closeAnimationDone;

    /// <summary>"Repeat: Off / All / One" label for the "…" menu's cycling item.</summary>
    public static readonly IValueConverter RepeatLabelConverter =
        new FuncValueConverter<RepeatMode, string>(mode => mode switch
        {
            RepeatMode.All => "Repeat: All",
            RepeatMode.One => "Repeat: One",
            _ => "Repeat: Off",
        });

    // Matches the slowest drawer transition (0.18s) so it stays mapped long enough
    // for the fade-out + slide to finish before IsVisible flips to false.
    private static readonly TimeSpan DrawerExitDuration = TimeSpan.FromMilliseconds(200);
    private Avalonia.Threading.DispatcherTimer? _drawerHideTimer;

    private Avalonia.Threading.DispatcherTimer? _lyricsScrollTimer;

    private MiniPlayerViewModel? Vm => DataContext as MiniPlayerViewModel;

    public MiniPlayerWindow()
    {
        InitializeComponent();

        // Per-pixel transparency only — OS acrylic (AcrylicBlur) tints the WHOLE
        // window rect, which painted the transparent corners outside the rounded
        // card as black squares. The simulated glass layers in XAML carry the look.
        //
        // Linux/X11: per-pixel transparency depends on a running compositor and
        // Avalonia doesn't track compositor changes (AvaloniaUI/Avalonia#3300), so
        // the surface can render as garbage/see-through instead (issue #26).
        // Request an opaque window there and paint it in the card's own color.
        if (OperatingSystem.IsLinux())
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            Background = new SolidColorBrush(Color.Parse("#FF141418"));
        }
        else
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        }

        // Seek commits follow the same BeginSeek/EndSeek protocol as the playback bar
        // so drags update the UI live and send a single debounced seek on release.
        // Every form has its own seek slider; wire all of them the same way.
        foreach (var slider in new[] { SeekSlider, BarSeekSlider, LargeSeekSlider, LyricsSeekSlider })
        {
            slider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel);
            slider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Tunnel);
            slider.PointerCaptureLost += (_, _) => Vm?.Player.EndSeek();
        }

        // Volume sliders commit once on release so VLC isn't hammered with rapid
        // changes mid-drag (anti-crackle), mirroring the playback bar.
        foreach (var slider in new[] { LargeVolumeSlider, DrawerVolumeSlider })
        {
            slider.AddHandler(PointerReleasedEvent, (_, _) => Vm?.Player.CommitVolume(), RoutingStrategies.Tunnel);
            slider.PointerCaptureLost += (_, _) => Vm?.Player.CommitVolume();
        }

        // The "…" menu has light dismiss disabled (it would unmap without the close
        // animation); any press inside the window closes it with the animation instead,
        // and the press is swallowed — the first click only dismisses the menu.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!MorePopup.IsOpen) return;
            CloseMorePopup();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // Clicking another app entirely never reaches the handler above.
        Deactivated += (_, _) => CloseMorePopup();

        // Space toggles play/pause app-wide; a focused Button would otherwise eat the
        // KeyDown and click on KeyUp, so both are tunneled (and the search box excluded).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        // Border.ClipToBounds clips to the rectangular bounds only, so the artwork /
        // acrylic layers would bleed past the rounded outline; these keep the rounded
        // clip geometries in sync with the (now resizable) window.
        RootPanel.SizeChanged += (_, e) =>
        {
            RootPanel.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 23, 23);
            DrawerSheet.MaxHeight = Math.Max(120, e.NewSize.Height * 0.62);
        };
        LargeArtClip.SizeChanged += (_, e) =>
        {
            LargeArtClip.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 14, 14);
        };
        LyricsArtClip.SizeChanged += (_, e) =>
        {
            LyricsArtClip.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 14, 14);
        };

        // WrapPanel word lines must wrap at the visible width — the scroller's padding
        // is not subtracted from the measure width (see the lyrics panel's MaxWidth).
        LyricsScroll.SizeChanged += (_, e) =>
        {
            LyricsItems.MaxWidth = Math.Max(120, e.NewSize.Width - 16);
        };

        // The form follows the window size; ClientSize covers both live resize drags
        // and programmatic jumps (menu → Lyrics).
        Resized += (_, _) => Vm?.UpdateFromSize(ClientSize.Width, ClientSize.Height);

        DataContextChanged += (_, _) => HookViewModel();

        // The root border starts faded/scaled-down in XAML; flipping the values once
        // the window is shown lets its transitions play the open animation.
        Opened += (_, _) =>
        {
            RootBorder.Opacity = 1;
            RootBorder.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");

            Vm?.UpdateFromSize(ClientSize.Width, ClientSize.Height);
            UpdateLyricsSurfaceRegistration();
            if (Vm?.IsLyricsForm == true)
                OnEnteredLyricsForm();
        };
    }

    // ── ViewModel wiring ─────────────────────────────────────

    private MiniPlayerViewModel? _hookedVm;

    private void HookViewModel()
    {
        if (_hookedVm != null)
        {
            _hookedVm.PropertyChanged -= OnVmPropertyChanged;
            _hookedVm.Lyrics.PropertyChanged -= OnLyricsPropertyChanged;
            _hookedVm.FormResizeRequested -= OnFormResizeRequested;
        }

        _hookedVm = Vm;
        if (_hookedVm == null) return;

        _hookedVm.PropertyChanged += OnVmPropertyChanged;
        _hookedVm.Lyrics.PropertyChanged += OnLyricsPropertyChanged;
        _hookedVm.FormResizeRequested += OnFormResizeRequested;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MiniPlayerViewModel.Drawer):
                OnDrawerChanged();
                break;
            case nameof(MiniPlayerViewModel.Form):
                UpdateLyricsSurfaceRegistration();
                if (Vm?.IsLyricsForm == true)
                    OnEnteredLyricsForm();
                break;
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex) && Vm?.IsLyricsForm == true)
            CenterActiveLyric(animated: true);
    }

    private void OnFormResizeRequested(MiniPlayerForm form)
    {
        var (w, h) = MiniPlayerViewModel.CanonicalSize(form);
        Width = w;
        Height = h;
    }

    // ── Close / lifecycle ────────────────────────────────────

    // Any close path (close button, toggling from the player bar) first plays the
    // reverse fade/scale, then really closes once the animation has run.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeAnimationDone)
        {
            e.Cancel = true;
            _closeAnimationDone = true;
            RootBorder.Opacity = 0;
            RootBorder.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.92)");
            Avalonia.Threading.DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(CloseAnimationMs));
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _drawerHideTimer?.Stop();
        _lyricsScrollTimer?.Stop();
        if (_lyricsSurfaceRegistered && _hookedVm != null)
        {
            _lyricsSurfaceRegistered = false;
            _hookedVm.Lyrics.SetLyricsSurfaceVisible(false);
            _hookedVm.Lyrics.SetWordClockHost(null);
        }
        if (_hookedVm != null)
        {
            _hookedVm.PropertyChanged -= OnVmPropertyChanged;
            _hookedVm.Lyrics.PropertyChanged -= OnLyricsPropertyChanged;
            _hookedVm.FormResizeRequested -= OnFormResizeRequested;
            _hookedVm = null;
        }
        base.OnClosed(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // ── Input ────────────────────────────────────────────────

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e) =>
        Vm?.Player.BeginSeek();

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        Vm?.Player.EndSeek();

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (MorePopup.IsOpen)
                CloseMorePopup();
            else if (Vm?.IsDrawerOpen == true)
                Vm.CloseDrawerCommand.Execute(null);
            else
                Close();
            e.Handled = true;
            return;
        }

        // Typing a space in the search box stays typing a space.
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && e.Source is not TextBox)
        {
            Vm?.Player.PlayPauseCommand.Execute(null);
            _spaceShortcutConsumed = true;
            e.Handled = true;
        }
    }

    /// <summary>
    /// True between the Space press we consumed as play/pause and its release. Button
    /// raises Click on key *up*, so swallowing only the press still let the focused
    /// button fire on the way back up.
    /// </summary>
    private bool _spaceShortcutConsumed;

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_spaceShortcutConsumed) return;
        _spaceShortcutConsumed = false;
        e.Handled = true;
    }

    // The window has no title bar, so any press on the glass (not on a control) drags it.
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Visual source)
        {
            foreach (var ancestor in source.GetSelfAndVisualAncestors())
            {
                if (ancestor is Button or Slider or TextBox)
                    return;
                if (ancestor is Border b && b.Classes.Contains("resize-grip"))
                    return;
            }
        }

        BeginMoveDrag(e);
    }

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (sender is not Border grip || grip.Tag is not string tag)
            return;

        var edge = tag switch
        {
            "North" => WindowEdge.North,
            "South" => WindowEdge.South,
            "East" => WindowEdge.East,
            "West" => WindowEdge.West,
            "NorthEast" => WindowEdge.NorthEast,
            "NorthWest" => WindowEdge.NorthWest,
            "SouthEast" => WindowEdge.SouthEast,
            _ => WindowEdge.SouthWest,
        };

        e.Handled = true;
        BeginResizeDrag(edge, e);
    }

    // ── "…" menu popup ───────────────────────────────────────

    // Mirrors the Settings modal: fade + scale(0.96 → 1) over 0.18s on open, the
    // reverse on close, and the popup unmaps once the exit animation has run.
    private int _menuCloseGeneration;

    private void OnMoreMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        // Pressing the "…" button while its menu is open already closed it in the
        // window's tunnel handler (which swallows the press, so no Click arrives).
        _menuCloseGeneration++;

        // The large-icon form's button sits low on the card, so its menu opens upward.
        var openUp = Vm?.IsLargeIconForm == true;
        MorePopup.Placement = openUp ? PlacementMode.TopEdgeAlignedRight : PlacementMode.BottomEdgeAlignedRight;
        MorePopup.VerticalOffset = openUp ? -6 : 6;
        MorePopup.PlacementTarget = anchor;

        MenuCard.Opacity = 0;
        MenuCard.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)");
        MorePopup.IsOpen = true;

        // Apply the shown target on the next render tick so the transitions animate
        // from the hidden state instead of snapping.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!MorePopup.IsOpen) return;
            MenuCard.Opacity = 1;
            MenuCard.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private void OnMenuItemClick(object? sender, RoutedEventArgs e) => CloseMorePopup();

    private void CloseMorePopup()
    {
        if (!MorePopup.IsOpen) return;

        var generation = ++_menuCloseGeneration;
        MenuCard.Opacity = 0;
        MenuCard.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.96)");
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (generation == _menuCloseGeneration)
                MorePopup.IsOpen = false;
        }, TimeSpan.FromMilliseconds(200));
    }

    // ── Drawer (bottom-sheet layers) ─────────────────────────

    // Open: fade + slide-up like the playback bar's flyouts. Close: reverse, and unmap
    // only once the exit transition has finished so it doesn't snap.
    private void OnDrawerChanged()
    {
        if (Vm?.IsDrawerOpen == true)
        {
            _drawerHideTimer?.Stop();
            _drawerHideTimer = null;

            DrawerSheet.Opacity = 0;
            SetDrawerOffset(14);
            DrawerSheet.IsVisible = true;

            // Apply the shown target on the next render tick so the transition animates
            // from the hidden state instead of snapping.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (Vm?.IsDrawerOpen != true) return;
                DrawerSheet.Opacity = 1;
                SetDrawerOffset(0);
                if (Vm.IsSearchDrawer)
                    SearchBox.Focus();
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
        else
        {
            DrawerSheet.Opacity = 0;
            SetDrawerOffset(14);

            _drawerHideTimer?.Stop();
            _drawerHideTimer = new Avalonia.Threading.DispatcherTimer { Interval = DrawerExitDuration };
            _drawerHideTimer.Tick += (_, _) =>
            {
                _drawerHideTimer?.Stop();
                _drawerHideTimer = null;
                DrawerSheet.IsVisible = false;
            };
            _drawerHideTimer.Start();
        }
    }

    private void SetDrawerOffset(double y)
    {
        if (DrawerSheet.RenderTransform is TranslateTransform t)
            t.Y = y;
    }

    // ── Lyrics form ──────────────────────────────────────────

    /// <summary>
    /// The lyrics VM only runs its sync/word clocks while a lyrics surface is
    /// attached, and the word clock rides the main window's render loop — which
    /// pumps no frames while the main window is hidden behind the mini player.
    /// The lyrics form therefore registers as a surface AND as the frame source.
    /// </summary>
    private bool _lyricsSurfaceRegistered;

    private void UpdateLyricsSurfaceRegistration()
    {
        var vm = Vm;
        var active = vm?.IsLyricsForm == true;
        if (active == _lyricsSurfaceRegistered) return;
        if (vm == null) { _lyricsSurfaceRegistered = false; return; }

        _lyricsSurfaceRegistered = active;
        if (active)
        {
            vm.Lyrics.SetWordClockHost(this);
            vm.Lyrics.SetLyricsSurfaceVisible(true);
        }
        else
        {
            vm.Lyrics.SetLyricsSurfaceVisible(false);
            vm.Lyrics.SetWordClockHost(null);
        }
    }

    private void OnEnteredLyricsForm()
    {
        var vm = Vm;
        if (vm == null) return;

        // Handles the case where the track started before the lyrics form existed.
        vm.Lyrics.EnsureLyricsForCurrentTrack();

        // Jump (no animation) once the list has laid out.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => CenterActiveLyric(animated: false),
            Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Scrolls the active lyric line to the vertical center of the panel.</summary>
    private void CenterActiveLyric(bool animated)
    {
        var vm = Vm;
        if (vm == null) return;

        var index = vm.Lyrics.ActiveLineIndex;
        if (index < 0) return;

        var container = LyricsItems.ContainerFromIndex(index);
        if (container == null) return;

        var top = container.TranslatePoint(new Point(0, 0), LyricsItems);
        if (top == null) return;

        var target = top.Value.Y + container.Bounds.Height / 2 - LyricsScroll.Viewport.Height / 2;
        var max = Math.Max(0, LyricsScroll.Extent.Height - LyricsScroll.Viewport.Height);
        target = Math.Clamp(target, 0, max);

        if (!animated)
        {
            _lyricsScrollTimer?.Stop();
            _lyricsScrollTimer = null;
            LyricsScroll.Offset = new Vector(0, target);
            return;
        }

        // Exponential chase (same idea as the lyrics page / SmoothScrollBehavior): a
        // new active line only moves the goalpost, so back-to-back updates during a
        // timeline scrub retarget the glide mid-flight instead of restarting it from
        // zero velocity — the flow never stutters.
        _lyricsChaseTarget = target;
        if (_lyricsScrollTimer != null) return;

        _lyricsScrollTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _lyricsScrollTimer.Tick += (_, _) =>
        {
            var current = LyricsScroll.Offset.Y;
            var remaining = _lyricsChaseTarget - current;
            if (Math.Abs(remaining) < 0.5)
            {
                LyricsScroll.Offset = new Vector(0, _lyricsChaseTarget);
                _lyricsScrollTimer?.Stop();
                _lyricsScrollTimer = null;
                return;
            }
            LyricsScroll.Offset = new Vector(0, current + remaining * 0.10);
        };
        _lyricsScrollTimer.Start();
    }

    private double _lyricsChaseTarget;
}

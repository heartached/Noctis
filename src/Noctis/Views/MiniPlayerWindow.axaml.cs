using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
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

    // Matches the slowest drawer transition (0.18s) so it stays mapped long enough
    // for the fade-out + slide to finish before IsVisible flips to false.
    private static readonly TimeSpan DrawerExitDuration = TimeSpan.FromMilliseconds(200);
    private Avalonia.Threading.DispatcherTimer? _drawerHideTimer;

    private Avalonia.Threading.DispatcherTimer? _lyricsScrollTimer;

    // ── Form morphing ──
    // Poses the form roots animate between. Hidden forms sit at Enter, so showing one is
    // just "ease to Rest"; the one leaving eases to Exit and is collapsed afterwards.
    private static readonly Avalonia.Media.Transformation.TransformOperations FormPoseRest =
        Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
    private static readonly Avalonia.Media.Transformation.TransformOperations FormPoseEnter =
        Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.03)");
    private static readonly Avalonia.Media.Transformation.TransformOperations FormPoseExit =
        Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.97)");
    // Must match the .form-root transition duration in XAML.
    private static readonly TimeSpan FormFadeDuration = TimeSpan.FromMilliseconds(180);
    private MiniPlayerForm? _visibleForm;

    // ── Eased window resize (menu jumps) ──
    private int _resizeAnimationGeneration;
    private bool _suppressPlacementCapture;

    // ── Drawer expansion ──
    // The drawer adds height to the WINDOW rather than covering the player, so these track
    // how much of the current geometry belongs to it. Placement is persisted with both
    // backed out, or quitting with the queue open would restore an oversized empty card.
    private double _drawerHeight;
    private double _drawerShiftY;

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
            // A popup's input routes through its parent window too, so this handler
            // also sees presses *inside* the menu. Swallowing those killed every menu
            // item (the button never got the press, so no Click ever fired) — leave
            // them alone and let the item's own Click handler close the menu.
            if (e.Source is Visual source &&
                (source == MenuCard || MenuCard.IsVisualAncestorOf(source)))
                return;

            CloseMorePopup();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // Clicking another app entirely never reaches the handler above.
        Deactivated += (_, _) => CloseMorePopup();

        // Space toggles play/pause app-wide; a focused Button would otherwise eat the
        // KeyDown and click on KeyUp, so both are tunneled (and the search box excluded).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);

        // RootBorder's ClipToBounds is off (so the close badge can overhang the corner),
        // which makes these explicit geometries the only thing holding the artwork /
        // acrylic layers to the rounded outline. Kept in sync with the resizable window.
        RootPanel.SizeChanged += (_, e) =>
        {
            // Radius = RootBorder.CornerRadius - BorderThickness, so the clip follows the
            // inside of the stroke.
            RootPanel.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 26.5, 26.5);
            // No MaxHeight any more: the drawer no longer competes with the player for the
            // card's height, it adds its own (AnimateDrawer).
        };
        // LargeArtClip / LyricsArtClip need no clip of their own any more: both forms'
        // artwork is now full bleed, so RootPanel's rounded clip above is what rounds it.

        // WrapPanel word lines must wrap at the visible width — the scroller's padding
        // is not subtracted from the measure width (see the lyrics panel's MaxWidth).
        LyricsScroll.SizeChanged += (_, e) =>
        {
            LyricsItems.MaxWidth = Math.Max(120, e.NewSize.Width - 16);
        };

        // The form follows the window size; ClientSize covers both live resize drags
        // and programmatic jumps (menu → Lyrics).
        Resized += (_, _) =>
        {
            // The drawer's share of the height is NOT part of the form decision — a Bar at
            // 172 plus a 200px queue is 372 and would otherwise silently become a Card.
            Vm?.UpdateFromSize(ClientSize.Width, ClientSize.Height - _drawerHeight);
            // An eased jump raises Resized on every frame; persisting each one is pure
            // churn, so the animation captures once when it lands instead.
            if (!_suppressPlacementCapture)
                CapturePlacement();
        };

        // Drag-move and resize both need persisting, and neither is covered by close:
        // the main window shutting down closes this window *after* it has already saved.
        PositionChanged += (_, _) => CapturePlacement();

        DataContextChanged += (_, _) => HookViewModel();

        // The root border starts faded/scaled-down in XAML; flipping the values once
        // the window is shown lets its transitions play the open animation.
        Opened += (_, _) =>
        {
            RootBorder.Opacity = 1;
            RootBorder.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");

            Vm?.UpdateFromSize(ClientSize.Width, ClientSize.Height);
            // The roots all start hidden (the .form-root style), and UpdateFromSize only
            // raises Form when it CHANGES — so the opening form has to be shown explicitly
            // or the card comes up empty.
            SyncFormVisual();
            UpdateLyricsSurfaceRegistration();
            if (Vm?.IsLyricsForm == true)
                OnEnteredLyricsForm();

            // Only trust Position/ClientSize once the platform window exists; before that
            // they are the pre-realize values and would overwrite a good stored placement.
            _placementTrackable = true;
        };
    }

    // ── Placement persistence ────────────────────────────────

    /// <summary>
    /// False until <see cref="Window.Opened"/>, so the restore that MainWindow applies
    /// before Show() isn't immediately overwritten by a pre-realize resize event.
    /// </summary>
    private bool _placementTrackable;

    /// <summary>
    /// Hands the current geometry to the settings view model, which debounces the write.
    /// Size is in DIPs and position in screen pixels — the same units MainWindow restores
    /// them in.
    /// </summary>
    private void CapturePlacement()
    {
        if (!_placementTrackable) return;
        // Persist the COLLAPSED geometry: back out the drawer's height and any upward shift
        // it needed to stay on screen, so reopening restores the player, not the expansion.
        Vm?.Settings.SetMiniPlayerPlacement(
            ClientSize.Width, ClientSize.Height - _drawerHeight,
            Position.X, Position.Y + _drawerShiftY);
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

        // A DataContext arriving after Opened would otherwise leave every form hidden.
        SyncFormVisual();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MiniPlayerViewModel.Drawer):
                OnDrawerChanged();
                break;
            case nameof(MiniPlayerViewModel.Form):
                SyncFormVisual();
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
        AnimateSizeTo(w, h);
    }

    /// <summary>
    /// Eases the window to a size instead of snapping to it, so a menu jump reads as the
    /// card growing into the new form. The form cross-fade rides along for free: every
    /// step raises Resized, so the threshold gets crossed partway through the glide.
    /// </summary>
    private void AnimateSizeTo(double targetWidth, double targetHeight)
    {
        var generation = ++_resizeAnimationGeneration;

        var fromWidth = Width;
        var fromHeight = Height;
        if (!double.IsFinite(fromWidth) || !double.IsFinite(fromHeight))
        {
            Width = targetWidth;
            Height = targetHeight;
            return;
        }

        const double durationMs = 280;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _suppressPlacementCapture = true;

        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        timer.Tick += (_, _) =>
        {
            // A newer jump owns the flag now, and a closing window must not be resized.
            if (generation != _resizeAnimationGeneration || _closeAnimationDone)
            {
                timer.Stop();
                return;
            }

            var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);   // CubicEaseOut, matching the card's own curve
            Width = fromWidth + (targetWidth - fromWidth) * eased;
            Height = fromHeight + (targetHeight - fromHeight) * eased;

            if (t < 1) return;

            timer.Stop();
            _suppressPlacementCapture = false;
            CapturePlacement();
        };
        timer.Start();
    }

    /// <summary>
    /// Cross-fades the form layouts. Both are alive for the length of the fade, which is
    /// why the roots' IsVisible is driven here rather than bound to IsXForm — a binding
    /// would collapse the outgoing tree in the same frame and leave nothing to fade.
    /// </summary>
    private void SyncFormVisual()
    {
        if (Vm is not { } vm) return;

        var next = vm.Form;
        if (_visibleForm == next) return;

        var previous = _visibleForm;
        _visibleForm = next;

        if (previous is { } prev)
        {
            var outgoing = FormRoot(prev);
            outgoing.Opacity = 0;
            outgoing.RenderTransform = FormPoseExit;
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                // Guarded by identity, not a generation counter: if the drag wandered back
                // over the threshold this form is on screen again, and a generation guard
                // would have left the *other* one stranded visible at opacity 0.
                if (_visibleForm == prev) return;
                outgoing.IsVisible = false;
                outgoing.RenderTransform = FormPoseEnter;
            }, FormFadeDuration);
        }

        var incoming = FormRoot(next);
        incoming.IsVisible = true;
        // One frame parked at the enter pose, so the transition has a start value to
        // animate from rather than jumping straight to rest.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_visibleForm != next) return;
            incoming.Opacity = 1;
            incoming.RenderTransform = FormPoseRest;
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    private Control FormRoot(MiniPlayerForm form) => form switch
    {
        MiniPlayerForm.Icon => IconFormRoot,
        MiniPlayerForm.Bar => BarFormRoot,
        MiniPlayerForm.Card => CardFormRoot,
        MiniPlayerForm.LargeIcon => LargeIconFormRoot,
        _ => LyricsFormRoot,
    };

    // ── Close / lifecycle ────────────────────────────────────

    // Any close path (close button, toggling from the player bar) first plays the
    // reverse fade/scale, then really closes once the animation has run.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeAnimationDone)
        {
            e.Cancel = true;
            _closeAnimationDone = true;
            // Last good geometry, before teardown can report anything odd.
            CapturePlacement();
            _placementTrackable = false;
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
        if (!ShouldBeginWindowDrag(e.Source))
            return;

        BeginMoveDrag(e);
    }

    /// <summary>
    /// True when a press landed on the window's own glass rather than on a control.
    /// The "…" menu lives in a popup whose input still routes through this window, so
    /// presses inside it are rejected outright — otherwise a press on the menu's padding
    /// would drag the window out from under the open menu. (The popup is a separate
    /// window on desktop and an overlay layer elsewhere; the menu-card check covers both.)
    /// </summary>
    internal bool ShouldBeginWindowDrag(object? source)
    {
        if (source is not Visual visual)
            return true;
        if (visual == MenuCard || MenuCard.IsVisualAncestorOf(visual))
            return false;
        if (visual.GetVisualRoot() is Visual root && !ReferenceEquals(root, this))
            return false;

        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ancestor is Button or Slider or TextBox)
                return false;
            if (ancestor is Border b && b.Classes.Contains("resize-grip"))
                return false;
        }

        return true;
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
    /// <summary>How much window height each drawer asks for. Fixed rather than
    /// content-sized so the card always grows by the same amount; long queues and result
    /// lists scroll inside their own section.</summary>
    private static double DrawerTargetHeight(MiniDrawer drawer) => drawer switch
    {
        MiniDrawer.Queue => 200,
        MiniDrawer.Search => 240,
        MiniDrawer.Volume => 64,
        _ => 0,
    };

    private void OnDrawerChanged()
    {
        var open = Vm?.IsDrawerOpen == true;
        var target = open ? DrawerTargetHeight(Vm!.Drawer) : 0;

        _drawerHideTimer?.Stop();
        _drawerHideTimer = null;

        if (open)
        {
            DrawerSheet.IsVisible = true;
            DrawerSheet.Opacity = 0;
            SetDrawerOffset(14);
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
        }

        AnimateDrawer(target, onLanded: () =>
        {
            if (Vm?.IsDrawerOpen != true)
                DrawerSheet.IsVisible = false;
        });
    }

    /// <summary>
    /// Grows or shrinks the window by the drawer's height, moving the window and the
    /// drawer on the SAME eased curve. That lockstep is the point: window height and
    /// drawer height change by the same delta at the same time, so the player area above
    /// stays exactly the same size throughout and never reflows or changes form. Setting
    /// the drawer to its full height first would starve the form row to nothing until the
    /// window caught up.
    /// </summary>
    private void AnimateDrawer(double targetDrawerHeight, Action onLanded)
    {
        var fromDrawer = _drawerHeight;
        var delta = targetDrawerHeight - fromDrawer;

        var fromHeight = Height;
        var fromShift = _drawerShiftY;
        if (!double.IsFinite(fromHeight))
        {
            _drawerHeight = targetDrawerHeight;
            DrawerSheet.Height = targetDrawerHeight;
            onLanded();
            return;
        }

        // Growing downward can run off the screen; shift the window up by the overflow so
        // the whole expanded card stays visible, and give the shift back when it collapses.
        var targetShift = 0.0;
        if (delta > 0 && Screens.ScreenFromWindow(this) is { } screen)
        {
            var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            var bottom = Position.Y + (fromHeight + delta) * scaling;
            var overflow = bottom - screen.WorkingArea.Bottom;
            if (overflow > 0)
                targetShift = Math.Min(overflow / scaling, Math.Max(0, (Position.Y - screen.WorkingArea.Y) / scaling));
        }

        var generation = ++_resizeAnimationGeneration;
        const double durationMs = 240;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var startY = Position.Y;
        _suppressPlacementCapture = true;

        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (generation != _resizeAnimationGeneration || _closeAnimationDone)
            {
                timer.Stop();
                return;
            }

            var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);

            _drawerHeight = fromDrawer + delta * eased;
            DrawerSheet.Height = _drawerHeight;
            Height = fromHeight + delta * eased;

            var shift = fromShift + (targetShift - fromShift) * eased;
            if (Math.Abs(shift - _drawerShiftY) > 0.01)
            {
                Position = new PixelPoint(Position.X, (int)Math.Round(startY - (shift - fromShift)));
                _drawerShiftY = shift;
            }

            if (t < 1) return;

            timer.Stop();
            _drawerHeight = targetDrawerHeight;
            DrawerSheet.Height = targetDrawerHeight;
            _drawerShiftY = targetShift;
            _suppressPlacementCapture = false;
            onLanded();
            CapturePlacement();
        };
        timer.Start();
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

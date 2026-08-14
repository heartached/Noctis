using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Helpers;
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

    // Set when the window had to fall back to an opaque surface (no Linux compositor),
    // in which case the card is squared off and must stay that way through resizes.
    private bool _squareCard;

    // Matches the slowest drawer transition (0.18s) so it stays mapped long enough
    // for the fade-out + slide to finish before IsVisible flips to false.
    private static readonly TimeSpan DrawerExitDuration = TimeSpan.FromMilliseconds(200);
    private Avalonia.Threading.DispatcherTimer? _drawerHideTimer;

    private Avalonia.Threading.DispatcherTimer? _lyricsScrollTimer;

    // Debounce for the viewport-derived lyric font size (see LyricsScroll.SizeChanged).
    private Avalonia.Threading.DispatcherTimer? _lyricsFontTimer;
    private double _pendingLyricsFontSize = 21;

    // ── Flowing-light mesh (mini copy of the lyrics page's MeshBlobLayer) ──
    // Same drift math and palette plumbing as LyricsView; the timer only runs while
    // the Lyrics form, the Artwork background mode and the flowing-light setting are
    // all active, so the other forms never pay for it.
    private const int MeshFrameMs = 33;
    private Avalonia.Threading.DispatcherTimer? _meshTimer;
    private readonly System.Diagnostics.Stopwatch _meshClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly TranslateTransform _meshBlob1Transform = new();
    private readonly TranslateTransform _meshBlob2Transform = new();
    private readonly TranslateTransform _meshBlob3Transform = new();

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
        // Linux is the awkward case: per-pixel transparency needs a running compositor,
        // and Avalonia's X11 backend doesn't track compositor changes
        // (AvaloniaUI/Avalonia#3300), so without one the surface renders as
        // garbage/see-through (issue #26). This used to go opaque on ALL of Linux, which
        // fixed #26 but left every compositing desktop (KDE, GNOME, …) showing the
        // window's square corners as dark wedges outside the r=28 card. So: ask whether
        // there is actually a compositor, and only fall back when there isn't.
        //
        // NOCTIS_MINI_OPAQUE=1 forces the fallback, for a desktop where transparency
        // still misbehaves (same escape-hatch convention as NOCTIS_SOFTWARE_RENDER).
        var forceOpaque = Environment.GetEnvironmentVariable("NOCTIS_MINI_OPAQUE") == "1";
        if (forceOpaque || (OperatingSystem.IsLinux() && !PlatformHelper.IsLinuxCompositorRunning()))
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            Background = new SolidColorBrush(Color.Parse("#FF141418"));
            // Square the card to match the window it can't see past. Rounding it here
            // buys nothing — the corners outside the arc are opaque either way — and
            // reads as a broken card rather than a deliberate one.
            RootBorder.CornerRadius = new CornerRadius(0);
            _squareCard = true;
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

        // Time readouts show only while the pointer is over the card (style class
        // "pointer-in", see Window.Styles). The WINDOW's enter/leave, not any child's,
        // so moving between controls inside the card never blinks them.
        PointerEntered += (_, _) => Classes.Set("pointer-in", true);
        PointerExited += (_, _) => Classes.Set("pointer-in", false);

        // RootBorder's ClipToBounds is off (so the close badge can overhang the corner),
        // which makes these explicit geometries the only thing holding the artwork /
        // acrylic layers to the rounded outline. Kept in sync with the resizable window.
        RootPanel.SizeChanged += (_, e) =>
        {
            // Radius = RootBorder.CornerRadius - BorderThickness, so the clip follows the
            // inside of the stroke. On the opaque fallback the card is squared off, so
            // the clip has to square off with it or the glass layers would pull away
            // from the corners the border now paints straight through.
            var radius = _squareCard ? 0 : 26.5;
            RootPanel.Clip = new RectangleGeometry(
                new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
            // No MaxHeight any more: the drawer no longer competes with the player for the
            // card's height, it adds its own (AnimateDrawer).
        };
        // LargeArtClip / LyricsArtClip need no clip of their own any more: both forms'
        // artwork is now full bleed, so RootPanel's rounded clip above is what rounds it.

        MiniMeshBlob1.RenderTransform = _meshBlob1Transform;
        MiniMeshBlob2.RenderTransform = _meshBlob2Transform;
        MiniMeshBlob3.RenderTransform = _meshBlob3Transform;

        // WrapPanel word lines must wrap at the visible width — the scroller's padding
        // is not subtracted from the measure width (see the lyrics panel's MaxWidth).
        LyricsScroll.SizeChanged += (_, e) =>
        {
            LyricsItems.MaxWidth = Math.Max(120, e.NewSize.Width - 16);
            // Lyric size follows the viewport, the same way the page derives its 46px
            // from a ~1000px window: 21px suits the canonical 640x412 lyrics form and
            // larger windows get proportionally larger text. The clamp keeps lines
            // wrapping instead of overflowing at either extreme, and MaxWidth above
            // already tracks the viewport, so bigger text just wraps sooner.
            // Debounced, NOT applied per tick: the open/close ease resizes the window
            // every ~16ms, and a FontSize write re-measures every lyric line (word
            // cells, marquees, wrap panels) — doing that mid-ease stuttered the whole
            // transition. MaxWidth stays per-tick (wrap must track the live width);
            // the font settles once, right after the size stops moving.
            var fontScale = Math.Clamp(
                Math.Min(e.NewSize.Height / 390.0, e.NewSize.Width / 350.0), 0.85, 1.6);
            _pendingLyricsFontSize = Math.Round(21 * fontScale);
            if (_lyricsFontTimer == null)
            {
                _lyricsFontTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(120)
                };
                _lyricsFontTimer.Tick += (_, _) =>
                {
                    _lyricsFontTimer!.Stop();
                    if (Math.Abs(LyricsItems.FontSize - _pendingLyricsFontSize) >= 0.5)
                        LyricsItems.FontSize = _pendingLyricsFontSize;
                    // Re-anchor AFTER the settled layout. The entry jump in
                    // OnEnteredLyricsForm runs while the open ease is still resizing
                    // the window, and the font write above re-measures every line —
                    // either one moves the extent out from under the saved offset,
                    // which left the current lyric parked off-screen after a
                    // close/reopen. Loaded priority so the re-wrap has happened.
                    if (Vm?.IsLyricsForm == true)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (Vm?.IsLyricsForm == true)
                                CenterActiveLyric(animated: false);
                        }, Avalonia.Threading.DispatcherPriority.Loaded);
                };
            }
            _lyricsFontTimer.Stop();
            _lyricsFontTimer.Start();
            // Run-out padding scaled to the viewport (the window is freely resizable):
            // enough headroom above the first line for it to reach the anchor, and
            // enough tail below the last line to scroll up past it. Without the top
            // half of this, the offset clamp at 0 stalled the first half-viewport of
            // lines against the top edge, where the presenter's clip cut them off.
            var v = e.NewSize.Height;
            LyricsStack.Margin = new Thickness(
                0, Math.Round(v * LyricsAnchorRatio), 8, Math.Round(v * (1 - LyricsAnchorRatio)) + 24);
        };

        // The form follows the window size; ClientSize covers both live resize drags
        // and programmatic jumps (menu → Lyrics).
        Resized += (_, _) =>
        {
            // While a drawer animation drives Window.Height, the sheet takes exactly the
            // height the window has ACTUALLY gained — platform resizes land a frame after
            // the property is set, and sizing the sheet ahead of the real resize squeezed
            // the player area above it on every tick, which read as the card shaking.
            if (_drawerAnim is { } anim && anim.Generation == _resizeAnimationGeneration)
            {
                _drawerHeight = Math.Clamp(ClientSize.Height - anim.FormHeight, 0, anim.MaxDrawer);
                DrawerSheet.Height = _drawerHeight;
            }

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
            UpdateMeshAnimationState();
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
            _hookedVm.Player.PropertyChanged -= OnPlayerPropertyChanged;
            _hookedVm.FormResizeRequested -= OnFormResizeRequested;
        }

        _hookedVm = Vm;
        if (_hookedVm == null) return;

        _lastVmForm = _hookedVm.Form;
        _hookedVm.PropertyChanged += OnVmPropertyChanged;
        _hookedVm.Lyrics.PropertyChanged += OnLyricsPropertyChanged;
        _hookedVm.Player.PropertyChanged += OnPlayerPropertyChanged;
        _hookedVm.FormResizeRequested += OnFormResizeRequested;

        // A DataContext arriving after Opened would otherwise leave every form hidden.
        SyncFormVisual();
        UpdateMeshAnimationState();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MiniPlayerViewModel.Drawer):
                OnDrawerChanged();
                break;
            case nameof(MiniPlayerViewModel.Form):
                // Leaving the split view ends the lyrics session the pre-lyrics capture
                // belonged to. The menu close consumes the capture BEFORE the form flips
                // (OnFormResizeRequested), so anything still here when Lyrics goes away
                // is a leftover from a resize-out — and "restoring" it on a later menu
                // close re-inflated the window to an obsolete size (the card came back
                // huge after hiding lyrics).
                if (_lastVmForm == MiniPlayerForm.Lyrics && Vm?.Form != MiniPlayerForm.Lyrics)
                    _preLyricsSize = null;
                _lastVmForm = Vm?.Form;

                SyncFormVisual();
                UpdateLyricsSurfaceRegistration();
                UpdateMeshAnimationState();
                if (Vm?.IsLyricsForm == true)
                    OnEnteredLyricsForm();
                break;
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.ActiveLineIndex) when Vm?.IsLyricsForm == true:
                CenterActiveLyric(animated: true);
                break;
            case nameof(LyricsViewModel.MeshBlobColor1):
            case nameof(LyricsViewModel.MeshBlobColor2):
            case nameof(LyricsViewModel.MeshBlobColor3):
                ApplyMeshColors();
                break;
            case nameof(LyricsViewModel.IsColorModeArtwork):
                UpdateMeshAnimationState();
                break;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The blob layer's visibility is bound in XAML; this only parks/resumes the timer.
        if (e.PropertyName == nameof(PlayerViewModel.LyricsFlowingLightEnabled))
            UpdateMeshAnimationState();
    }

    // ── Flowing-light mesh background ──
    // Mini copy of the lyrics page's drifting palette blobs (issue #22): the same
    // never-looping sine paths and breathing, behind the lyrics COLUMN only. See
    // LyricsView.OnMeshTick for the full notes on the constants.

    private void UpdateMeshAnimationState()
    {
        if (Vm is { IsLyricsForm: true } vm && vm.Lyrics.IsColorModeArtwork &&
            vm.Player.LyricsFlowingLightEnabled)
            StartMeshAnimation();
        else
            StopMeshAnimation();
    }

    private void StartMeshAnimation()
    {
        if (_meshTimer != null) return;
        ApplyMeshColors();
        _meshTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MeshFrameMs)
        };
        _meshTimer.Tick += OnMeshTick;
        _meshTimer.Start();
    }

    private void StopMeshAnimation()
    {
        if (_meshTimer == null) return;
        _meshTimer.Stop();
        _meshTimer.Tick -= OnMeshTick;
        _meshTimer = null;
    }

    private void OnMeshTick(object? sender, EventArgs e)
    {
        // Geometry tracks the blob layer's own bounds (the lyrics column, not the
        // window); re-deriving it every tick doubles as the resize hook, same as
        // the page.
        var size = MiniMeshBlobLayer.Bounds.Size;
        var w = size.Width;
        var h = size.Height;
        if (w <= 0 || h <= 0) return;

        PlaceMeshBlob(MiniMeshBlob1, w * 0.90, -w * 0.20, -h * 0.30);
        PlaceMeshBlob(MiniMeshBlob2, w * 0.75,  w * 0.45,  h * 0.40);
        PlaceMeshBlob(MiniMeshBlob3, w * 0.60,  w * 0.30, -h * 0.15);

        var t = _meshClock.Elapsed.TotalSeconds;

        _meshBlob1Transform.X = Math.Sin(t * 0.110) * w * 0.14;
        _meshBlob1Transform.Y = Math.Cos(t * 0.083) * h * 0.12;
        _meshBlob2Transform.X = Math.Sin(t * 0.071 + 2.1) * w * 0.16;
        _meshBlob2Transform.Y = Math.Cos(t * 0.127 + 0.7) * h * 0.14;
        _meshBlob3Transform.X = Math.Sin(t * 0.093 + 4.2) * w * 0.18;
        _meshBlob3Transform.Y = Math.Cos(t * 0.059 + 1.3) * h * 0.16;

        MiniMeshBlob1.Opacity = 0.68 + 0.22 * Math.Sin(t * 0.151);
        MiniMeshBlob2.Opacity = 0.66 + 0.24 * Math.Sin(t * 0.101 + 2.6);
        MiniMeshBlob3.Opacity = 0.62 + 0.26 * Math.Sin(t * 0.131 + 5.0);
    }

    private static void PlaceMeshBlob(Avalonia.Controls.Shapes.Ellipse blob, double diameter, double left, double top)
    {
        // Width starts as NaN (unset), and NaN comparisons are false — check explicitly.
        if (double.IsNaN(blob.Width) || Math.Abs(blob.Width - diameter) > 0.5)
        {
            blob.Width = diameter;
            blob.Height = diameter;
        }
        Canvas.SetLeft(blob, left);
        Canvas.SetTop(blob, top);
    }

    /// <summary>Retints the three blob gradients in place whenever the VM re-derives
    /// the artwork palette (same mutate-in-place pattern as the page).</summary>
    private void ApplyMeshColors()
    {
        if (Vm is not { } vm) return;
        SetMeshBlobColor(MiniMeshBlob1, vm.Lyrics.MeshBlobColor1);
        SetMeshBlobColor(MiniMeshBlob2, vm.Lyrics.MeshBlobColor2);
        SetMeshBlobColor(MiniMeshBlob3, vm.Lyrics.MeshBlobColor3);
    }

    private static void SetMeshBlobColor(Avalonia.Controls.Shapes.Ellipse blob, Color color)
    {
        if (blob.Fill is not RadialGradientBrush brush || brush.GradientStops.Count < 3) return;
        brush.GradientStops[0].Color = Color.FromArgb(0xD8, color.R, color.G, color.B);
        brush.GradientStops[1].Color = Color.FromArgb(0x60, color.R, color.G, color.B);
        brush.GradientStops[2].Color = Color.FromArgb(0x00, color.R, color.G, color.B);
    }

    /// <summary>Exact window size (and the form it belonged to) captured when the menu
    /// jumped into the Lyrics form, so closing lyrics restores the size the user actually
    /// had — the canonical size read as "the mini player forgot my size". Lives only as
    /// long as that lyrics session: resizing out of the split view clears it (see the
    /// Form case in OnVmPropertyChanged).</summary>
    private (MiniPlayerForm Form, double Width, double Height)? _preLyricsSize;

    /// <summary>Previous VM form, kept to detect the Lyrics→other edge above.</summary>
    private MiniPlayerForm? _lastVmForm;

    private void OnFormResizeRequested(MiniPlayerForm form)
    {
        if (form == MiniPlayerForm.Lyrics)
        {
            if (Vm is { Form: not MiniPlayerForm.Lyrics } vm &&
                double.IsFinite(Width) && double.IsFinite(Height))
                _preLyricsSize = (vm.Form, Width, Height);
        }
        else if (_preLyricsSize is { } saved && saved.Form == form)
        {
            // Only when the VM is returning to the form the size was captured in —
            // the Icon→Card fallback still gets Card's canonical size below.
            _preLyricsSize = null;
            AnimateSizeTo(saved.Width, saved.Height);
            return;
        }

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

        // Render priority + whole-DIP steps: the default Background priority starves
        // under layout churn (irregular jumps), and fractional sizes shimmer the
        // border/clip on every tick — together they read as the card shaking.
        var timer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Render)
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
            Width = Math.Round(fromWidth + (targetWidth - fromWidth) * eased);
            Height = Math.Round(fromHeight + (targetHeight - fromHeight) * eased);

            if (t < 1) return;

            timer.Stop();
            Width = targetWidth;
            Height = targetHeight;
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
        _lyricsFontTimer?.Stop();
        StopMeshAnimation();
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
            _hookedVm.Player.PropertyChanged -= OnPlayerPropertyChanged;
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

        // The tick only eases Window.Height (whole DIPs — fractional sizes make the
        // border/clip shimmer); the Resized handler sizes the sheet from the height the
        // window has ACTUALLY reached, keeping the form row constant (see there).
        _drawerAnim = (generation, fromHeight - fromDrawer, Math.Max(fromDrawer, targetDrawerHeight));

        // Render priority: the default (Background) starves under layout churn and the
        // eased glide degrades into irregular jumps.
        var timer = new Avalonia.Threading.DispatcherTimer(Avalonia.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        timer.Tick += (_, _) =>
        {
            if (generation != _resizeAnimationGeneration || _closeAnimationDone)
            {
                // A newer animation owns _drawerAnim (its generation differs) — leave it.
                timer.Stop();
                return;
            }

            var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);

            Height = Math.Round(fromHeight + delta * eased);

            var shift = fromShift + (targetShift - fromShift) * eased;
            if (Math.Abs(shift - _drawerShiftY) > 0.01)
            {
                Position = new PixelPoint(Position.X, (int)Math.Round(startY - (shift - fromShift)));
                _drawerShiftY = shift;
            }

            if (t < 1) return;

            timer.Stop();
            _drawerAnim = null;
            Height = fromHeight + delta;
            _drawerHeight = targetDrawerHeight;
            DrawerSheet.Height = targetDrawerHeight;
            _drawerShiftY = targetShift;
            _suppressPlacementCapture = false;
            onLanded();
            CapturePlacement();
        };
        timer.Start();
    }

    /// <summary>In-flight drawer animation: generation tag (only honored while it matches
    /// the live animation), the constant player-area height above the sheet, and the
    /// ceiling for the sheet height. Consumed by the Resized handler.</summary>
    private (int Generation, double FormHeight, double MaxDrawer)? _drawerAnim;

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

    /// <summary>Anchor for the active lyric line, as a fraction of the viewport height —
    /// slightly above centre, like the Apple Music mini player. The run-out padding in
    /// LyricsScroll.SizeChanged is derived from the same ratio so every line can reach it.</summary>
    private const double LyricsAnchorRatio = 0.42;

    /// <summary>Scrolls the active lyric line to the anchor point of the panel.</summary>
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

        // Offset lives in extent space, which includes the wrapper StackPanel's top
        // margin (the run-out padding) — TranslatePoint against LyricsItems does not,
        // so add it back or every target lands short by the pad height (the lyrics
        // page corrects the same way with LyricsItemsControl.Margin.Top).
        var target = top.Value.Y + LyricsStack.Margin.Top + container.Bounds.Height / 2
                     - LyricsScroll.Viewport.Height * LyricsAnchorRatio;
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

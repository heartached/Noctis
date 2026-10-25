using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Side lyrics panel. Pure presentation over the shared <see cref="LyricsViewModel"/>
/// (which loads lyrics and runs the sync timer independently of any view); this
/// code-behind only keeps the active line anchored in the panel viewport.
/// Follow state is panel-local so it never fights the full lyrics page's
/// IsAutoFollowPaused / Follow button.
/// </summary>
public partial class LyricsPanelView : UserControl
{
    private LyricsViewModel? _vm;
    private int _lastScrolledIndex = -1;
    // Bumped to invalidate any in-flight frame-clock scroll animation (replaces stopping a timer).
    private int _scrollAnimationGeneration;
    private DispatcherTimer? _followResumeTimer;
    private bool _isProgrammaticScroll;
    private bool _followPaused;

    // Cascade tuning (mirrors the lyrics page): each line below the active one starts
    // its glide this much later, with the stagger depth capped — the Apple Music
    // "settle top-down" feel.
    private const double CascadeDelayPerLineMs = LineMotion.CascadeDelayPerLineMs;
    private const int CascadeMaxLines = 8;
    // A line's lag may exceed the line above's by at most this much. Uncapped, the
    // stagger displaced a line by up to a full line height relative to its neighbour
    // (worst at the old cascade cut-off, where the last staggered line slid clean over
    // the first unstaggered one on every multi-line scroll) — lyrics rendered on top
    // of each other mid-glide. 16px stays under both views' inter-line gaps.
    private const double MaxCascadeLagStepPx = 16;
    private HashSet<Control>? _cascadeLines;
    // The glide in flight (or last run): a new line change hands its velocities on.
    private LineGlide? _glide;

    /// <summary>True while this panel is counted in the VM's visible-surface tally.</summary>
    private bool _countedAsVisible;

    /// <summary>
    /// False while the side panel is closed. The view is built once and stays attached
    /// (closing only sets the wrapper's IsVisible=false, which does not detach it), so
    /// attach/detach alone never took the panel out of the visible-surface tally.
    /// </summary>
    private bool _shown = true;

    // Flowing-artwork background: the same animator as the lyrics page and the mini
    // player, so the panel's backdrop drifts and pulses in step with them.
    private readonly FlowingArtworkAnimator _flow;

    public LyricsPanelView()
    {
        InitializeComponent();

        _flow = new FlowingArtworkAnimator(this, PanelFlowBackdrop, PanelFlowLayer1, PanelFlowLayer2, PanelBeatGlow, GetBeatContext);

        DataContextChanged += (_, _) => HookViewModel();
        AttachedToVisualTree += (_, _) =>
        {
            HookViewModel();

            // Count this panel as a visible lyrics surface so the VM's sync timer and
            // per-frame word clock run only while something can display them.
            if (_shown && !_countedAsVisible && _vm != null)
            {
                _vm.SetLyricsSurfaceVisible(true);
                _countedAsVisible = true;
            }

            if (_vm is { ScrollLineIndex: >= 0 } vm)
                JumpToLineWhenReady(vm.ScrollLineIndex);
            UpdateFlowAnimationState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelScrollAnimation();
            CancelFollowResumeTimer();
            _flow.Enabled = false;

            if (_countedAsVisible)
            {
                _countedAsVisible = false;
                _vm?.SetLyricsSurfaceVisible(false);
            }
        };

        PanelScrollViewer.PointerWheelChanged += OnUserScroll;
        // Ctrl/⌘+wheel shifts the lyrics instead of scrolling (GitHub #102); tunnel on the
        // view so it runs before SmoothScrollBehavior's handler on the ScrollViewer.
        AddHandler(PointerWheelChangedEvent, OnLyricsOffsetWheel, RoutingStrategies.Tunnel);
    }

    private void HookViewModel()
    {
        if (ReferenceEquals(_vm, DataContext)) return;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.LyricsSwapPending -= OnLyricsSwapPending;
            _vm.LyricsSwapped -= OnLyricsSwapped;
            _vm.Player.PropertyChanged -= OnPlayerPropertyChanged;
        }
        _vm = DataContext as LyricsViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.LyricsSwapPending += OnLyricsSwapPending;
            _vm.LyricsSwapped += OnLyricsSwapped;
            // The Settings toggle lives on the Player VM (same live channel as the page).
            _vm.Player.PropertyChanged += OnPlayerPropertyChanged;
        }
        _lastScrolledIndex = -1;
        UpdateFlowAnimationState();
    }

    /// <summary>
    /// Called by MainWindow when the side panel opens (true) and once it has slid shut
    /// and its wrapper is hidden (false). Keeps the VM's visible-surface tally and the
    /// flowing backdrop tied to whether the panel is on screen, so a closed panel stops
    /// holding the sync timer, the per-frame word clock and the flow's visibility poll.
    /// </summary>
    internal void SetShown(bool shown)
    {
        _shown = shown;
        if (shown && !_countedAsVisible && _vm != null && this.VisualRoot != null)
        {
            _vm.SetLyricsSurfaceVisible(true);
            _countedAsVisible = true;
        }
        else if (!shown && _countedAsVisible)
        {
            _countedAsVisible = false;
            _vm?.SetLyricsSurfaceVisible(false);
        }
        UpdateFlowAnimationState();
    }

    // ── Flowing-artwork background ──
    // Runs while the panel is attached and shown, the Artwork background mode is on
    // and the Settings toggle is on.

    private void UpdateFlowAnimationState()
    {
        _flow.BeatReactive = Noctis.Models.FlowingStyles.IsBeatReactive(
            Noctis.Models.FlowingStyles.Normalize(_vm?.Player.LyricsFlowingStyle));
        _flow.Speed = (_vm?.Player.LyricsDriftMovement ?? Noctis.Models.AppSettings.LyricsDriftKnobDefault) / 100.0;
        _flow.Enabled = _vm != null && this.VisualRoot != null && _shown
                        && _vm.IsColorModeArtwork && _vm.Player.LyricsFlowingLightEnabled;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.LyricsFlowingLightEnabled)
                or nameof(PlayerViewModel.LyricsFlowingStyle)
                or nameof(PlayerViewModel.LyricsDriftMovement))
            UpdateFlowAnimationState();
    }

    private BeatContext GetBeatContext()
    {
        if (_vm is not { Player: { } player }) return default;
        return new BeatContext(player.CurrentTrack?.Bpm ?? 0, player.Position.TotalMilliseconds, player.IsPlaying);
    }

    // ── Track-change lyric swap (mirrors the lyrics page): fade out, let the
    // wholesale rebuild + re-anchor happen while hidden, fade back in. The
    // re-anchor itself rides the existing ActiveLyricLines handler below.

    private bool _lyricsSwapInProgress;

    private Action? _closeOverride;

    /// <summary>
    /// Set by a host that shows this panel itself (the Lyrics Studio's preview pane, 10-14): the X
    /// calls it, with its own tooltip, instead of closing the main window's side panel.
    /// </summary>
    internal void SetCloseAction(Action close, string tooltip)
    {
        _closeOverride = close;
        ToolTip.SetTip(PanelCloseButton, tooltip);
    }

    /// <summary>
    /// Closes the panel by clearing the flag on the main VM, which drives the
    /// wrapper's width animation — the same close path the Escape key takes.
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_closeOverride is { } close)
        {
            close();
            return;
        }
        var mainWindow = this.FindLogicalAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainVm)
            mainVm.IsLyricsPanelOpen = false;
    }

    private void OnLyricsSwapPending(object? sender, EventArgs e)
    {
        _lyricsSwapInProgress = true;
        FadeLyricsHost(0.0, LyricsViewModel.LyricsSwapFadeOutMs);
    }

    private void OnLyricsSwapped(object? sender, EventArgs e)
    {
        _lyricsSwapInProgress = false;
        FadeLyricsHost(1.0, 240);
    }

    private void FadeLyricsHost(double to, int durationMs)
    {
        if (PanelLyricsHost is not { } host) return;
        host.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                Easing = new Avalonia.Animation.Easings.CubicEaseInOut(),
            },
        };
        host.Opacity = to;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.IsColorModeArtwork))
        {
            UpdateFlowAnimationState();
            return;
        }

        // Skip anchoring work entirely while the panel is closed (wrapper hidden);
        // opening the panel re-anchors via EnsureLyricsForCurrentTrack.
        if (_vm == null || this.VisualRoot == null || !IsEffectivelyVisible) return;

        if (e.PropertyName == nameof(LyricsViewModel.ScrollLineIndex))
        {
            // Mid-swap index churn must not start an animated glide — the
            // ActiveLyricLines re-anchor below jumps once the swap lands.
            if (_lyricsSwapInProgress) return;
            if (_followPaused || !_vm.IsSyncTabSelected) return;
            if (_vm.ScrollLineIndex >= 0)
                ScrollToLine(_vm.ScrollLineIndex);
        }
        else if (e.PropertyName == nameof(LyricsViewModel.ActiveLyricLines))
        {
            // Track change or sync/plain switch — re-anchor from scratch.
            _lastScrolledIndex = -1;
            _followPaused = false;
            CancelFollowResumeTimer();
            var index = _vm.IsSyncTabSelected ? _vm.ScrollLineIndex : -1;
            if (index >= 0)
                JumpToLineWhenReady(index);
            else
                Dispatcher.UIThread.Post(
                    () => PanelScrollViewer.Offset = default,
                    DispatcherPriority.Loaded);
        }
    }

    // ── Manual-scroll pause: let the user read, then glide back ────────

    private void OnUserScroll(object? sender, PointerWheelEventArgs e)
    {
        if (_isProgrammaticScroll) return;
        CancelScrollAnimation();
        _followPaused = true;

        CancelFollowResumeTimer();
        _followResumeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _followResumeTimer.Tick += (_, _) =>
        {
            CancelFollowResumeTimer();
            _followPaused = false;
            if (_vm is { IsSyncTabSelected: true, ScrollLineIndex: >= 0 } vm)
                ScrollToLine(vm.ScrollLineIndex, force: true);
        };
        _followResumeTimer.Start();
    }

    private void OnLyricsOffsetWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!SmoothScrollBehavior.HasCommandModifier(e) || _vm == null) return;
        if (e.Source is not Visual source
            || !(source == PanelScrollViewer || PanelScrollViewer.IsVisualAncestorOf(source))) return;
        // Handled: no scroll, and OnUserScroll never pauses follow.
        if (_vm.NudgeLyricsOffset(e.Delta.Y))
            e.Handled = true;
    }

    private void CancelFollowResumeTimer()
    {
        _followResumeTimer?.Stop();
        _followResumeTimer = null;
    }

    // ── Scrolling ──────────────────────────────────────────────────────

    private Panel? GetLinesPanel()
    {
        var presenter = PanelItemsControl?.GetVisualDescendants()
            .OfType<ItemsPresenter>()
            .FirstOrDefault();
        return presenter?.GetVisualChildren().FirstOrDefault() as Panel;
    }

    private Control? FindLineControl(int index)
    {
        if (index < 0) return null;
        var panel = GetLinesPanel();
        if (panel == null || index >= panel.Children.Count) return null;
        return panel.Children[index];
    }

    private double? ComputeTargetOffset(int index)
    {
        var panel = GetLinesPanel();
        if (panel == null || index < 0 || index >= panel.Children.Count || PanelScrollViewer == null)
            return null;
        var target = panel.Children[index];

        var transform = target.TransformToVisual(panel);
        if (transform == null) return null;

        var childTop = transform.Value.Transform(new Point(0, 0)).Y;
        var childHeight = target.Bounds.Height;

        // Anchor the active line ~22% down the panel viewport (matches the lyrics page),
        // never past the end of the content — see LyricsScrollAnchor.
        return Helpers.LyricsScrollAnchor.ComputeAnchorOffset(
            childTop, childHeight,
            PanelScrollViewer.Viewport.Height,
            PanelScrollViewer.Extent.Height);
    }

    private void JumpToLineWhenReady(int index)
    {
        _lastScrolledIndex = index;
        CancelScrollAnimation();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var offset = ComputeTargetOffset(index);
                if (offset is { } y)
                    PanelScrollViewer.Offset = new Vector(0, y);
            }
            catch { }
        }, DispatcherPriority.Loaded);
    }

    private void ScrollToLine(int index, bool force = false)
    {
        if (!force && index == _lastScrolledIndex) return;
        _lastScrolledIndex = index;

        // Keep a glide in flight: the new one starts from what is on screen, velocities
        // included (see the page's ScrollToActiveLine). Early returns cancel properly.
        _scrollAnimationGeneration++;
        var started = false;

        // Start on the same frame as the line's own transitions — see the page's
        // ScrollToActiveLine for why the old 10ms settle timer read as a hesitation.
        try
        {
            var offset = ComputeTargetOffset(index);
            if (offset is not { } target) return;

            var current = PanelScrollViewer.Offset.Y;
            var diff = target - current;
            if (Math.Abs(diff) < 2 && _glide == null)
            {
                PanelScrollViewer.Offset = new Vector(0, target);
                return;
            }

            var hold = _vm != null ? LyricsTimeline.HoldMs(_vm.LyricLines, index) : double.NaN;
            AnimateScroll(current, target, LineMotion.ForHold(hold), GetLinesPanel(), index);
            started = true;
        }
        catch { }
        finally
        {
            if (!started) CancelScrollAnimation();
        }
    }

    // Frame-clock animation via TopLevel.RequestAnimationFrame: vsync-locked, unlike a
    // 16ms DispatcherTimer that beats against the compositor's ~16.7ms frame interval.
    // Mirrors the lyrics page's AnimateScroll: one LineGlide for the offset and every
    // line, a per-line stagger below the active line (transient translate that relaxes to
    // zero) so the stack settles top-down, and a glide in flight hands over its velocities.
    private void AnimateScroll(double from, double to, LineSpring spring,
        Panel? linesPanel = null, int activeIndex = -1)
    {
        _scrollAnimationGeneration++;
        _isProgrammaticScroll = true;

        var delta = to - from;
        // Every line below the active one takes part; only the DELAY is capped.
        // Cutting the list at CascadeMaxLines left the first excluded line static
        // while the last included one lagged a full line height onto it — visible
        // as overlapping lyrics on every multi-line scroll in the panel, whose tall
        // viewport keeps that boundary on screen. Capped-delay lines share one lag,
        // so they glide as a coherent block with no seam.
        var cascade = linesPanel != null && activeIndex >= 0 && Math.Abs(delta) > 8;
        var lines = linesPanel?.Children;
        var lineCount = lines?.Count ?? 0;
        var glide = LineGlide.Begin(_glide, NowMs(), from, to, spring, lineCount, activeIndex,
            cascade ? CascadeDelayPerLineMs : 0, CascadeMaxLines,
            i => lines![i].RenderTransform is TranslateTransform tt ? tt.Y : 0);
        _glide = glide;
        var touched = _cascadeLines ??= new HashSet<Control>();
        var generation = _scrollAnimationGeneration;

        void Frame(TimeSpan _)
        {
            // Superseded or cancelled: the canceller already reset flags/transforms.
            if (generation != _scrollAnimationGeneration) return;

            var now = NowMs();
            PanelScrollViewer.Offset = new Vector(0, glide.Offset(now));

            // Each line is displaced by the gap between the offset and its own course.
            // Chained clamp below the active line: on large scroll deltas the raw stagger
            // between neighbours exceeds the inter-line gap, so bound each line's lag to
            // its predecessor's (the list is in top-to-bottom order) — lines never cross.
            if (lines != null)
            {
                var prevLag = 0.0;
                var n = Math.Min(lineCount, lines.Count);
                for (var i = 0; i < n; i++)
                {
                    var lag = glide.LineLag(i, now);
                    if (i > activeIndex)
                    {
                        lag = LineMotion.CascadeStep(lag, prevLag, MaxCascadeLagStepPx);
                        prevLag = lag;
                    }
                    var control = lines[i];
                    if (control.RenderTransform is TranslateTransform tt)
                    {
                        // ReSharper disable once CompareOfFloatsByEqualityOperator
                        if (tt.Y != lag)
                        {
                            tt.Y = lag;
                            touched.Add(control);
                        }
                    }
                    else if (Math.Abs(lag) > 0.01)
                    {
                        control.RenderTransform = new TranslateTransform(0, lag);
                        touched.Add(control);
                    }
                }
            }

            if (glide.IsDone(now))
            {
                PanelScrollViewer.Offset = new Vector(0, to);
                ClearCascadeTransforms();
                _glide = null;
                _isProgrammaticScroll = false;
                return;
            }

            RequestScrollFrame(Frame, to);
        }

        RequestScrollFrame(Frame, to);
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    // Schedules the next animation frame; if the panel left the visual tree mid-animation
    // (no TopLevel → no frame callbacks), snaps to the target so the offset never strands.
    private void RequestScrollFrame(Action<TimeSpan> frame, double to)
    {
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(frame);
        }
        else
        {
            PanelScrollViewer.Offset = new Vector(0, to);
            CancelScrollAnimation();
        }
    }

    private void CancelScrollAnimation()
    {
        _scrollAnimationGeneration++;
        _isProgrammaticScroll = false;
        _glide = null;
        ClearCascadeTransforms();
    }

    private void ClearCascadeTransforms()
    {
        if (_cascadeLines == null) return;
        foreach (var control in _cascadeLines)
        {
            if (control.RenderTransform is TranslateTransform tt)
                tt.Y = 0;
        }
        _cascadeLines = null;
    }
}

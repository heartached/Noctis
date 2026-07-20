using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
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
    // its glide this much later, up to this many lines deep — the Apple Music
    // "settle top-down" feel.
    private const double CascadeDelayPerLineMs = 35;
    private const int CascadeMaxLines = 8;
    private List<(Control Control, double DelayMs)>? _cascadeLines;

    public LyricsPanelView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => HookViewModel();
        AttachedToVisualTree += (_, _) =>
        {
            HookViewModel();
            if (_vm is { ActiveLineIndex: >= 0 } vm)
                JumpToLineWhenReady(vm.ActiveLineIndex);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelScrollAnimation();
            CancelFollowResumeTimer();
        };

        PanelScrollViewer.PointerWheelChanged += OnUserScroll;
    }

    private void HookViewModel()
    {
        if (ReferenceEquals(_vm, DataContext)) return;
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as LyricsViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnVmPropertyChanged;
        _lastScrolledIndex = -1;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Skip anchoring work entirely while the panel is closed (wrapper hidden);
        // opening the panel re-anchors via EnsureLyricsForCurrentTrack.
        if (_vm == null || this.GetVisualRoot() == null || !IsEffectivelyVisible) return;

        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
        {
            if (_followPaused || !_vm.IsSyncTabSelected) return;
            if (_vm.ActiveLineIndex >= 0)
                ScrollToLine(_vm.ActiveLineIndex);
        }
        else if (e.PropertyName == nameof(LyricsViewModel.ActiveLyricLines))
        {
            // Track change or sync/plain switch — re-anchor from scratch.
            _lastScrolledIndex = -1;
            _followPaused = false;
            CancelFollowResumeTimer();
            var index = _vm.IsSyncTabSelected ? _vm.ActiveLineIndex : -1;
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
            if (_vm is { IsSyncTabSelected: true, ActiveLineIndex: >= 0 } vm)
                ScrollToLine(vm.ActiveLineIndex, force: true);
        };
        _followResumeTimer.Start();
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
        var viewportHeight = PanelScrollViewer.Viewport.Height;

        // Anchor the active line ~22% down the panel viewport (matches the lyrics page).
        var offset = childTop - (viewportHeight * 0.22) + (childHeight / 2.0);
        return Math.Max(0, offset);
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

        CancelScrollAnimation();

        // Small delay so layout settles after the active-line style change.
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                var offset = ComputeTargetOffset(index);
                if (offset is not { } target) return;

                var current = PanelScrollViewer.Offset.Y;
                var diff = target - current;
                if (Math.Abs(diff) < 2)
                {
                    PanelScrollViewer.Offset = new Vector(0, target);
                    return;
                }

                var distance = Math.Abs(diff);
                var durationMs = (int)Math.Min(1050, Math.Max(650, distance * 0.85));
                AnimateScroll(current, target, durationMs, GetLinesPanel(), index);
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    // Frame-clock animation via TopLevel.RequestAnimationFrame: vsync-locked, unlike a
    // 16ms DispatcherTimer that beats against the compositor's ~16.7ms frame interval.
    // Mirrors the lyrics page's AnimateScroll: smootherstep base glide with a per-line
    // stagger below the active line (transient translate that relaxes to zero), so the
    // stack settles top-down instead of moving as one rigid slab.
    private void AnimateScroll(double from, double to, int durationMs,
        Panel? linesPanel = null, int activeIndex = -1)
    {
        CancelScrollAnimation();
        _isProgrammaticScroll = true;

        var delta = to - from;
        var cascade = new List<(Control Control, double DelayMs)>();
        if (linesPanel != null && activeIndex >= 0 && Math.Abs(delta) > 8)
        {
            for (int i = activeIndex + 1;
                 i < linesPanel.Children.Count && i - activeIndex <= CascadeMaxLines;
                 i++)
            {
                cascade.Add((linesPanel.Children[i], (i - activeIndex) * CascadeDelayPerLineMs));
            }
        }
        _cascadeLines = cascade.Count > 0 ? cascade : null;

        var generation = _scrollAnimationGeneration;
        var stopwatch = Stopwatch.StartNew();
        var totalMs = (double)durationMs;
        var maxDelayMs = cascade.Count > 0 ? cascade[^1].DelayMs : 0;

        void Frame(TimeSpan _)
        {
            // Superseded or cancelled: the canceller already reset flags/transforms.
            if (generation != _scrollAnimationGeneration) return;

            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / totalMs);
            // Smootherstep glides in and out without overshoot.
            var eased = Easing.SmootherStep(t);
            PanelScrollViewer.Offset = new Vector(0, from + delta * eased);

            // Stagger: each cascade line is displaced by the gap between the base ease
            // and its own delayed ease — positive while catching up, zero when settled.
            foreach (var (control, delayMs) in cascade)
            {
                var tLine = Math.Clamp((elapsed - delayMs) / totalMs, 0.0, 1.0);
                var lag = delta * (eased - Easing.SmootherStep(tLine));
                if (control.RenderTransform is TranslateTransform tt)
                    tt.Y = lag;
                else
                    control.RenderTransform = new TranslateTransform(0, lag);
            }

            if (t >= 1.0 && elapsed >= totalMs + maxDelayMs)
            {
                PanelScrollViewer.Offset = new Vector(0, to);
                ClearCascadeTransforms();
                _isProgrammaticScroll = false;
                return;
            }

            RequestScrollFrame(Frame, to);
        }

        RequestScrollFrame(Frame, to);
    }

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
        ClearCascadeTransforms();
    }

    private void ClearCascadeTransforms()
    {
        if (_cascadeLines == null) return;
        foreach (var (control, _) in _cascadeLines)
        {
            if (control.RenderTransform is TranslateTransform tt)
                tt.Y = 0;
        }
        _cascadeLines = null;
    }
}

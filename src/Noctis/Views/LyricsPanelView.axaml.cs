using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private DispatcherTimer? _scrollTimer;
    private DispatcherTimer? _followResumeTimer;
    private bool _isProgrammaticScroll;
    private bool _followPaused;

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

    private Control? FindLineControl(int index)
    {
        if (PanelItemsControl == null || index < 0 || index >= PanelItemsControl.ItemCount)
            return null;
        var presenter = PanelItemsControl.GetVisualDescendants()
            .OfType<ItemsPresenter>()
            .FirstOrDefault();
        var panel = presenter?.GetVisualChildren().FirstOrDefault() as Panel;
        if (panel == null || index >= panel.Children.Count) return null;
        return panel.Children[index];
    }

    private double? ComputeTargetOffset(int index)
    {
        var target = FindLineControl(index);
        if (target == null || PanelScrollViewer == null) return null;

        var presenter = PanelItemsControl.GetVisualDescendants()
            .OfType<ItemsPresenter>()
            .FirstOrDefault();
        var panel = presenter?.GetVisualChildren().FirstOrDefault() as Panel;
        if (panel == null) return null;

        var transform = target.TransformToVisual(panel);
        if (transform == null) return null;

        var childTop = transform.Value.Transform(new Point(0, 0)).Y;
        var childHeight = target.Bounds.Height;
        var viewportHeight = PanelScrollViewer.Viewport.Height;

        // Anchor the active line ~30% down the panel viewport.
        var offset = childTop - (viewportHeight * 0.30) + (childHeight / 2.0);
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

                var durationMs = (int)Math.Min(900, Math.Max(450, Math.Abs(diff) * 0.9));
                AnimateScroll(current, target, durationMs);
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    private void AnimateScroll(double from, double to, int durationMs)
    {
        CancelScrollAnimation();
        _isProgrammaticScroll = true;

        var stopwatch = Stopwatch.StartNew();
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollTimer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, stopwatch.Elapsed.TotalMilliseconds / durationMs);
            // Smootherstep: glides in and out instead of jumping.
            var eased = t * t * t * (t * (t * 6 - 15) + 10);
            PanelScrollViewer.Offset = new Vector(0, from + (to - from) * eased);
            if (t >= 1.0)
                CancelScrollAnimation();
        };
        _scrollTimer.Start();
    }

    private void CancelScrollAnimation()
    {
        _scrollTimer?.Stop();
        _scrollTimer = null;
        _isProgrammaticScroll = false;
    }
}

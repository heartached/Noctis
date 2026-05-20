using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsCaptureView : UserControl
{
    private readonly DispatcherTimer _autoHideTimer;
    private Border? _controlPanel;

    private LyricsCaptureViewModel? _subscribedVm;
    private int _lastScrolledIndex = -1;
    private DispatcherTimer? _scrollAnimTimer;

    public LyricsCaptureView()
    {
        InitializeComponent();
        _controlPanel = this.FindControl<Border>("ControlPanel");

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoHideTimer.Tick += (_, _) => HideControlPanel();

        PointerMoved += OnPointerMoved;
        KeyDown += OnKeyDown;
        AttachedToVisualTree += (_, _) => Focus();
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += OnViewPropertyChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
        if (DataContext is LyricsCaptureViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            _subscribedVm = vm;
        }
    }

    // When the overlay becomes visible, jump straight to the current line (no glide).
    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true && _subscribedVm != null)
        {
            _lastScrolledIndex = -1;
            ScrollToActiveLine(_subscribedVm.ActiveLineIndex, animate: false);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsCaptureViewModel.ActiveLineIndex) && _subscribedVm != null)
            ScrollToActiveLine(_subscribedVm.ActiveLineIndex, animate: true);
    }

    private void ScrollToActiveLine(int index, bool animate)
    {
        if (index < 0)
        {
            _lastScrolledIndex = -1;
            return;
        }
        if (index == _lastScrolledIndex) return;
        _lastScrolledIndex = index;

        CancelScrollAnimation();

        // Brief delay so layout settles after the active line changes.
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (CaptureLyricsItems == null || index >= CaptureLyricsItems.ItemCount) return;

                var presenter = CaptureLyricsItems.GetVisualDescendants()
                    .OfType<ItemsPresenter>()
                    .FirstOrDefault();
                if (presenter == null) return;

                var panel = presenter.GetVisualChildren().FirstOrDefault() as Panel;
                if (panel == null || index >= panel.Children.Count) return;

                var targetChild = panel.Children[index];
                if (CaptureScrollViewer == null) return;

                var childBounds = targetChild.TransformToVisual(panel);
                if (childBounds == null) return;

                var childTop = childBounds.Value.Transform(new Point(0, 0)).Y;
                var childHeight = targetChild.Bounds.Height;
                var viewportHeight = CaptureScrollViewer.Viewport.Height;

                // Center the active line vertically.
                var targetOffset = childTop - (viewportHeight / 2.0) + (childHeight / 2.0);
                targetOffset = Math.Max(0, targetOffset);

                var currentOffset = CaptureScrollViewer.Offset.Y;
                var diff = Math.Abs(targetOffset - currentOffset);

                if (!animate || diff < 2)
                {
                    CaptureScrollViewer.Offset = new Vector(0, targetOffset);
                    return;
                }

                AnimateScroll(currentOffset, targetOffset,
                    (int)Math.Min(1050, Math.Max(650, diff * 0.85)));
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    private void AnimateScroll(double from, double to, int durationMs)
    {
        CancelScrollAnimation();
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollAnimTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (CaptureScrollViewer == null)
            {
                CancelScrollAnimation();
                return;
            }

            var elapsed = sw.Elapsed.TotalMilliseconds;
            var t = Math.Min(1.0, elapsed / durationMs);
            var eased = Easing.SmootherStep(t);
            CaptureScrollViewer.Offset = new Vector(0, from + (to - from) * eased);

            if (t >= 1.0)
            {
                CaptureScrollViewer.Offset = new Vector(0, to);
                CancelScrollAnimation();
            }
        };
        timer.Start();
    }

    private void CancelScrollAnimation()
    {
        _scrollAnimTimer?.Stop();
        _scrollAnimTimer = null;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowControlPanel();
        _autoHideTimer.Stop();
        _autoHideTimer.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is LyricsCaptureViewModel vm)
        {
            vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ShowControlPanel()
    {
        if (_controlPanel is null) return;
        _controlPanel.Opacity = 1;
        _controlPanel.IsHitTestVisible = true;
    }

    private void HideControlPanel()
    {
        _autoHideTimer.Stop();
        if (_controlPanel is null) return;
        _controlPanel.Opacity = 0;
        _controlPanel.IsHitTestVisible = false;
    }
}

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsPanelView : UserControl
{
    private LyricsViewModel? _subscribedVm;
    private int _lastScrolledIndex = -1;
    private DispatcherTimer? _scrollAnimTimer;

    public LyricsPanelView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (DataContext is LyricsViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            _subscribedVm = vm;

            // Initial scroll to current active line
            if (vm.ActiveLineIndex >= 0)
                ScrollToLine(vm.ActiveLineIndex);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
        CancelScrollAnimation();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not LyricsViewModel vm) return;

        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
        {
            if (vm.ActiveLineIndex >= 0)
                ScrollToLine(vm.ActiveLineIndex);
            else
                _lastScrolledIndex = -1;
        }
    }

    private void ScrollToLine(int index)
    {
        if (index == _lastScrolledIndex) return;
        _lastScrolledIndex = index;

        CancelScrollAnimation();

        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                if (PanelLyricsItems == null || index >= PanelLyricsItems.ItemCount) return;

                var presenter = PanelLyricsItems.GetVisualDescendants()
                    .OfType<ItemsPresenter>()
                    .FirstOrDefault();
                if (presenter == null) return;

                var panel = presenter.GetVisualChildren().FirstOrDefault() as Panel;
                if (panel == null || index >= panel.Children.Count) return;

                var targetChild = panel.Children[index];
                if (PanelScrollViewer == null) return;

                var childBounds = targetChild.TransformToVisual(panel);
                if (childBounds == null) return;

                var childTop = childBounds.Value.Transform(new Point(0, 0)).Y;
                var childHeight = targetChild.Bounds.Height;
                var viewportHeight = PanelScrollViewer.Viewport.Height;

                // Center the active line vertically
                var targetOffset = childTop - (viewportHeight / 2.0) + (childHeight / 2.0);
                targetOffset = Math.Max(0, targetOffset);

                var currentOffset = PanelScrollViewer.Offset.Y;
                var diff = Math.Abs(targetOffset - currentOffset);

                if (diff < 2)
                {
                    PanelScrollViewer.Offset = new Vector(0, targetOffset);
                    return;
                }

                AnimateScroll(currentOffset, targetOffset, (int)Math.Min(600, Math.Max(300, diff * 0.7)));
            }
            catch { }
        }, TimeSpan.FromMilliseconds(10));
    }

    private void AnimateScroll(double from, double to, int durationMs)
    {
        CancelScrollAnimation();
        var sw = Stopwatch.StartNew();
        _scrollAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollAnimTimer.Tick += (_, _) =>
        {
            var elapsed = sw.ElapsedMilliseconds;
            if (elapsed >= durationMs)
            {
                PanelScrollViewer.Offset = new Vector(0, to);
                CancelScrollAnimation();
                return;
            }
            var t = (double)elapsed / durationMs;
            var eased = Math.Sin(t * Math.PI / 2.0); // ease-out-sine
            PanelScrollViewer.Offset = new Vector(0, from + (to - from) * eased);
        };
        _scrollAnimTimer.Start();
    }

    private void CancelScrollAnimation()
    {
        _scrollAnimTimer?.Stop();
        _scrollAnimTimer = null;
    }
}

using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Compact always-on-top player window (artwork, track info, transport, progress).
/// Opened/closed by clicking the cover art in the bottom player bar; closing it
/// restores the main window (handled by <see cref="MainWindow.ToggleMiniPlayer"/>).
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const int CloseAnimationMs = 170;
    private bool _closeAnimationDone;

    public MiniPlayerWindow()
    {
        InitializeComponent();

        // Seek commits follow the same BeginSeek/EndSeek protocol as the playback bar
        // so drags update the UI live and send a single debounced seek on release.
        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.PointerCaptureLost += (_, _) => (DataContext as PlayerViewModel)?.EndSeek();

        // The root border starts faded/scaled-down in XAML; flipping the values
        // once the window is shown lets its transitions play the open animation.
        Opened += (_, _) =>
        {
            RootBorder.Opacity = 1;
            RootBorder.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
        };
    }

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

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e) =>
        (DataContext as PlayerViewModel)?.BeginSeek();

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        (DataContext as PlayerViewModel)?.EndSeek();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // The window has no title bar, so any press on the artwork (not on a control) drags it.
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is Avalonia.Visual source)
        {
            foreach (var ancestor in source.GetSelfAndVisualAncestors())
            {
                if (ancestor is Button or Slider)
                    return;
            }
        }

        BeginMoveDrag(e);
    }
}

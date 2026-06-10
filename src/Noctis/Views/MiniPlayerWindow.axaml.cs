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
    public MiniPlayerWindow()
    {
        InitializeComponent();

        // Seek commits follow the same BeginSeek/EndSeek protocol as the playback bar
        // so drags update the UI live and send a single debounced seek on release.
        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SeekSlider.PointerCaptureLost += (_, _) => (DataContext as PlayerViewModel)?.EndSeek();
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

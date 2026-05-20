using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsCaptureView : UserControl
{
    private readonly DispatcherTimer _autoHideTimer;
    private Border? _controlPanel;

    public LyricsCaptureView()
    {
        InitializeComponent();
        _controlPanel = this.FindControl<Border>("ControlPanel");

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoHideTimer.Tick += (_, _) => HideControlPanel();

        PointerMoved += OnPointerMoved;
        KeyDown += OnKeyDown;
        AttachedToVisualTree += (_, _) => Focus();
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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Noctis.Services.Plugins;

namespace Noctis.Views;

/// <summary>
/// API 1.3: the window a plugin's own view (<see cref="Noctis.Plugins.IPluginViewProvider"/>)
/// opens in. One window per view: while it is open, opening the view again brings it forward
/// (<see cref="PluginViewEntry.Reactivate"/>) instead of creating the control twice. Closes
/// itself when the plugin stops or withdraws the view.
/// </summary>
public sealed class PluginViewWindow : Window
{
    private readonly PluginViewEntry _view;
    private readonly Func<bool> _reactivate;

    private PluginViewWindow(PluginViewEntry view, Control content)
    {
        _view = view;
        Title = view.Name;
        Width = 1000;
        Height = 680;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x18));
        Content = content;
        view.Withdrawn += OnWithdrawn;
        _reactivate = BringForward;
        view.Reactivate = _reactivate;
    }

    /// <summary><see cref="PluginHost.ViewPresenter"/>: shows <paramref name="content"/> in a new window.</summary>
    public static bool Present(PluginViewEntry view, Control content)
    {
        var window = new PluginViewWindow(view, content);
        var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (main?.Icon is { } icon) window.Icon = icon;
        // Not owned: a view like a pet plaza is something to leave open beside the player, and
        // an owned window would minimize with (and always sit above) the main window.
        window.Show();
        return true;
    }

    private bool BringForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return true;
    }

    private void OnWithdrawn(object? sender, EventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _view.Withdrawn -= OnWithdrawn;
        if (ReferenceEquals(_view.Reactivate, _reactivate)) _view.Reactivate = null;
        // Detach the plugin's control so its DetachedFromVisualTree runs (timers stop) and a
        // closed window does not keep the plugin's load context alive.
        Content = null;
        base.OnClosed(e);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Noctis.Services.Plugins;

namespace Noctis.Views;

/// <summary>
/// API 1.3: the window a plugin's own view (<see cref="Noctis.Plugins.IPluginViewProvider"/>)
/// opens in. One window per view: while it is open, opening the view again brings it forward
/// (<see cref="PluginViewEntry.Reactivate"/>) instead of creating the control twice. Closes
/// itself when the plugin stops or withdraws the view. Since API 1.5 it opens at the size the
/// view asks for (<see cref="PluginViewEntry.ReadSizeHint"/>).
/// </summary>
public sealed class PluginViewWindow : Window
{
    /// <summary>The size a view that asks for none opens at (made for page-like views such as Album Town).</summary>
    public static readonly Size DefaultSize = new(1000, 680);

    /// <summary>The smallest the user can make a window whose view asks for no minimum (lowered to its preferred size when smaller).</summary>
    public static readonly Size DefaultMinSize = new(480, 360);

    /// <summary>No minimum (asked for or not) goes below this.</summary>
    public static readonly Size SmallestMinSize = new(200, 150);

    private readonly PluginViewEntry _view;
    private readonly Func<bool> _reactivate;
    private bool _unhooked;

    internal PluginViewWindow(PluginViewEntry view, Control content, Size? workArea = null)
    {
        _view = view;
        Title = view.Name;
        // Sized here, before Show: Window.ShowCore takes its size from Width/Height before the first
        // layout pass and writes it back afterwards, so a view resizing its window while it is
        // being attached (OnAttachedToVisualTree) is undone, and CenterScreen centres the old size.
        var size = Resolve(view.ReadSizeHint(), workArea);
        MinWidth = size.MinWidth;
        MinHeight = size.MinHeight;
        SizeToContent = size.SizeToContent;
        Width = size.SizeToContent.HasFlag(SizeToContent.Width) ? double.NaN : size.Width;
        Height = size.SizeToContent.HasFlag(SizeToContent.Height) ? double.NaN : size.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x12, 0x18));
        Content = content;
        view.Withdrawn += OnWithdrawn;
        _reactivate = BringForward;
        view.Reactivate = _reactivate;
    }

    /// <summary>The window's opening size and minimum, in DIPs.</summary>
    internal readonly record struct WindowSize(double Width, double Height, double MinWidth, double MinHeight, SizeToContent SizeToContent);

    /// <summary>
    /// The size a view's window opens at: the hint per axis (else <see cref="DefaultSize"/>), a
    /// minimum of the hint's (else <see cref="DefaultMinSize"/>, lowered to the preferred size), never
    /// under <see cref="SmallestMinSize"/>, and both within <paramref name="workArea"/> (DIPs) when known.
    /// </summary>
    internal static WindowSize Resolve(PluginViewSizeHint hint, Size? workArea)
    {
        static bool Ok(double v) => double.IsFinite(v) && v > 0;
        var width = hint.Preferred is { } p && Ok(p.Width) ? p.Width : DefaultSize.Width;
        var height = hint.Preferred is { } q && Ok(q.Height) ? q.Height : DefaultSize.Height;
        var minWidth = hint.Min is { } m && Ok(m.Width) ? m.Width : Math.Min(DefaultMinSize.Width, width);
        var minHeight = hint.Min is { } n && Ok(n.Height) ? n.Height : Math.Min(DefaultMinSize.Height, height);
        minWidth = Math.Max(SmallestMinSize.Width, minWidth);
        minHeight = Math.Max(SmallestMinSize.Height, minHeight);
        if (workArea is { } area && Ok(area.Width) && Ok(area.Height))
        {
            width = Math.Min(width, area.Width);
            height = Math.Min(height, area.Height);
            minWidth = Math.Min(minWidth, area.Width);
            minHeight = Math.Min(minHeight, area.Height);
        }
        return new WindowSize(Math.Max(width, minWidth), Math.Max(height, minHeight), minWidth, minHeight, hint.SizeToContent);
    }

    /// <summary><see cref="PluginHost.ViewPresenter"/>: shows <paramref name="content"/> in a new window.</summary>
    public static bool Present(PluginViewEntry view, Control content)
    {
        var main = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var screen = ScreenOf(main);
        var window = new PluginViewWindow(view, content, screen is null ? null : WorkAreaDips(screen));
        if (main?.Icon is { } icon) window.Icon = icon;
        // Not owned: a view like a pet plaza is something to leave open beside the player, and
        // an owned window would minimize with (and always sit above) the main window. Without an
        // owner, CenterScreen centres on the screen under the window's position (by default the
        // primary one), so start it on Noctis's screen.
        if (screen is not null) window.Position = screen.WorkingArea.Center;
        try
        {
            window.Show();
        }
        catch
        {
            // The view threw while it was laid out: without this the dead window stayed the view's
            // Reactivate, so every later Open "succeeded" by activating a window that never showed.
            try { window.Close(); } catch { /* not shown far enough to close */ }
            window.Unhook();
            throw;
        }
        return true;
    }

    private static Screen? ScreenOf(Window? main)
    {
        if (main is null) return null;
        try { return main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary; }
        catch (Exception) { return null; } // no screen information (headless)
    }

    private static Size WorkAreaDips(Screen screen)
    {
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        return new Size(screen.WorkingArea.Width / scale, screen.WorkingArea.Height / scale);
    }

    private bool BringForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return true;
    }

    private void OnWithdrawn(object? sender, EventArgs e) => Close();

    private void Unhook()
    {
        if (_unhooked) return;
        _unhooked = true;
        _view.Withdrawn -= OnWithdrawn;
        if (ReferenceEquals(_view.Reactivate, _reactivate)) _view.Reactivate = null;
        // Detach the plugin's control so its DetachedFromVisualTree runs (timers stop) and a
        // closed window does not keep the plugin's load context alive.
        Content = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        Unhook();
        base.OnClosed(e);
    }
}

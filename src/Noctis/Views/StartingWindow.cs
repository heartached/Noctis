using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Noctis.Localization;

namespace Noctis.Views;

/// <summary>
/// Shown while the audio engine is still starting (see <see cref="Helpers.AudioWarmupGate"/>),
/// so a slow first start after an update shows a window instead of nothing. Closed by the app
/// once the main window is up; the user cannot close it, as there is nothing to cancel yet.
/// </summary>
internal sealed class StartingWindow : Window
{
    private bool _allowClose;

    public StartingWindow()
    {
        Title = Loc.T("Main.Noctis");
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Noctis.UI/Assets/Icons/Noctis.ico"))); }
        catch { /* the title bar keeps the default icon */ }
        Width = 320;
        Height = 150;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Bind(BackgroundProperty, this.GetResourceObservable("AppWindowBackgroundBrush"));

        Content = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(32, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.T("Main.Noctis"),
                    FontSize = 22,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                new ProgressBar { IsIndeterminate = true },
            },
        };
    }

    /// <summary>Closes the window for good (the main window is showing, or the app quits).</summary>
    public void CloseForMainWindow()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The title bar's close button and Alt+F4 only; app shutdown and the OS still close it.
        if (!_allowClose && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
            e.Cancel = true;
        base.OnClosing(e);
    }
}

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Every resources-changed notification on the application walks every control of every window
/// (hidden kept pages included) and re-resolves its DynamicResources. A theme switch made three
/// or four of them (theme overlay out and in, accent overlay out and in), each landing a
/// half-switched state (11-27, ThemeSwitchBench); it now makes one, and re-applying what is
/// already on screen ("Accent follows album art", the switch's own accent refresh) makes none.
/// </summary>
public class ThemeSwitchNotificationTests
{
    [AvaloniaFact]
    public void ASwitch_NotifiesOnce_AndReapplyingNotifiesNothing()
    {
        var host = Application.Current!;
        var oldVariant = host.RequestedThemeVariant;
        var app = new Noctis.App { ThemeHost = host };
        int changes = 0, accents = 0;
        EventHandler<ResourcesChangedEventArgs> onChanged = (_, _) => changes++;
        EventHandler onAccent = (_, _) => accents++;
        host.ResourcesChanged += onChanged;
        Noctis.App.AccentApplied += onAccent;
        try
        {
            app.SetAccent("#E74856");
            app.SetTheme(Noctis.App.ThemeGray);

            (int, int) Count(Action a)
            {
                changes = accents = 0;
                a();
                return (changes, accents);
            }

            Assert.Equal((1, 1), Count(() => app.SetTheme(Noctis.App.ThemeInk)));
            Assert.Equal((1, 1), Count(() => app.SetTheme(Noctis.App.ThemeSmoke)));
            Assert.Equal((0, 0), Count(() => app.SetTheme(Noctis.App.ThemeSmoke)));
            Assert.Equal((0, 0), Count(() => app.SetAccent("#E74856")));
            Assert.Equal((1, 1), Count(() => app.SetAccent("#2A7FCF")));
            Assert.Equal((1, 1), Count(() => app.SetTheme(Noctis.App.ThemeLight)));
            Assert.Equal(ThemeVariant.Light, host.RequestedThemeVariant);
            Assert.Equal((1, 1), Count(() => app.SetTheme(Noctis.App.ThemeGray)));
            Assert.Equal(ThemeVariant.Dark, host.RequestedThemeVariant);
            Assert.Equal((1, 1), Count(() => app.SetTintWindowWithAccent(true)));
            Assert.Equal((1, 1), Count(() => app.SetTheme(Noctis.App.ThemeMidnight)));
        }
        finally
        {
            host.ResourcesChanged -= onChanged;
            Noctis.App.AccentApplied -= onAccent;
            foreach (var layer in host.Resources.MergedDictionaries.OfType<Noctis.Helpers.ResourceLayer>().ToList())
                host.Resources.MergedDictionaries.Remove(layer);
            host.RequestedThemeVariant = oldVariant;
        }
    }

    private static void Invoke(MainWindow w, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(w, args);

    /// <summary>With Liquid Glass on, the window's glass overlay (the theme's main surface at 35%)
    /// follows the theme inside the switch's one notification, and the window's own refresh
    /// after the switch (its ThemeChanged handler) finds nothing left to do.</summary>
    [AvaloniaFact]
    public void WithLiquidGlass_TheWindowFollowsTheTheme_InsideTheSameNotification()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var host = Application.Current!;
        var oldVariant = host.RequestedThemeVariant;
        var app = new Noctis.App { ThemeHost = host };
        var main = new MainWindow { Width = 900, Height = 600 };
        main.Show();
        Dispatcher.UIThread.RunJobs();
        // As MainWindow wires it once loaded with its view model.
        EventHandler follow = (_, _) => Invoke(main, "FollowThemeWithLiquidGlass");
        Noctis.App.ThemeResourcesApplying += follow;
        var windowChanges = 0;
        try
        {
            app.SetAccent("#E74856");
            app.SetTheme(Noctis.App.ThemeGray);
            Invoke(main, "ApplyLiquidGlass", true);
            main.ResourcesChanged += (_, _) => windowChanges++;

            foreach (var theme in new[] { Noctis.App.ThemeInk, Noctis.App.ThemeLight, Noctis.App.ThemeMidnight })
            {
                windowChanges = 0;
                app.SetTheme(theme);
                Assert.Equal(1, windowChanges);
                Assert.True(host.TryGetResource("AppMainBackground", host.ActualThemeVariant, out var themed));
                Assert.True(main.TryFindResource("AppMainBackground", main.ActualThemeVariant, out var glassed));
                var glass = Assert.IsAssignableFrom<ISolidColorBrush>(glassed);
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(themed).Color, glass.Color);
                Assert.Equal(0.35, glass.Opacity, 3);

                windowChanges = 0;
                Invoke(main, "ApplyLiquidGlass", true);
                Assert.Equal(0, windowChanges);
            }
        }
        finally
        {
            Noctis.App.ThemeResourcesApplying -= follow;
            Invoke(main, "ApplyLiquidGlass", false);
            main.Close();
            foreach (var layer in host.Resources.MergedDictionaries.OfType<Noctis.Helpers.ResourceLayer>().ToList())
                host.Resources.MergedDictionaries.Remove(layer);
            host.RequestedThemeVariant = oldVariant;
        }
    }
}

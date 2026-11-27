using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
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
}

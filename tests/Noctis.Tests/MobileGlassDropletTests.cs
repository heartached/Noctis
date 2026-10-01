using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's Liquid Glass (Apple Music, iOS 26): the bar, mini player, folded bubbles, Queue
/// pill and header buttons are a lightly frosted glass whose rim refracts what scrolls under
/// it, and the selected tab's pill is a glass droplet that lifts while it travels between tabs
/// and settles back into a soft grey pill.
/// </summary>
public class MobileGlassDropletTests
{
    private static void PumpUntil(Func<bool> done, double seconds = 3)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(15);
        }
    }

    [AvaloniaFact]
    public void Droplet_AtRest_IsAPlainGreyPill_ThatReadsNoBackdrop()
    {
        var droplet = new GlassDroplet { Background = new SolidColorBrush(Colors.Black, 0.07), BlurRadius = 0 };
        Assert.False(droplet.HasLens);
        Assert.False(droplet.NeedsBackdrop);
        Assert.Equal(0, droplet.Specular);
        Assert.Equal(0, droplet.BoxShadow.Count);
        Assert.Null(droplet.GlassTintOpacity);
        var scale = Assert.IsType<ScaleTransform>(droplet.RenderTransform);
        Assert.Equal(1, scale.ScaleX);
    }

    [AvaloniaFact]
    public void Droplet_Lifted_IsClearMagnifyingGlass_WithARainbowRim_AndSettlesBackExactly()
    {
        var droplet = new GlassDroplet { Background = new SolidColorBrush(Colors.Black, 0.07), BlurRadius = 0 };
        droplet.Lift = 1;

        Assert.True(droplet.HasLens);
        Assert.Equal(GlassDroplet.LiftedBand, droplet.Refraction);
        Assert.Equal(GlassDroplet.LiftedBend, droplet.RefractionAmount);
        Assert.Equal(GlassDroplet.LiftedZoom, droplet.Magnification, 6);
        Assert.Equal(1, droplet.Dispersion);
        Assert.Equal(1, droplet.Specular);
        Assert.True(droplet.GlassTintOpacity < 0.07 * 0.2, $"tint {droplet.GlassTintOpacity}: the droplet is not clear");
        Assert.Equal(1, droplet.BoxShadow.Count);
        var scale = (ScaleTransform)droplet.RenderTransform!;
        Assert.True(scale.ScaleX > 1 && scale.ScaleY > scale.ScaleX, $"{scale.ScaleX} × {scale.ScaleY}");

        droplet.Lift = 0.5;
        Assert.InRange(droplet.Refraction, 0.1, GlassDroplet.LiftedBand - 0.1);

        droplet.Lift = 0;
        Assert.False(droplet.HasLens);
        Assert.False(droplet.NeedsBackdrop);
        Assert.Equal(0, droplet.Specular);
        Assert.Equal(0, droplet.Dispersion);
        Assert.Equal(0, droplet.BoxShadow.Count);
        Assert.Null(droplet.GlassTintOpacity);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, scale.ScaleY);
    }

    [AvaloniaFact]
    public void TappingAnotherTab_LiftsTheDroplet_SlidesIt_ThenSettlesItOnTheNewTab()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var pill = view.FindControl<GlassDroplet>("SelectionPill")!;
        var host = view.FindControl<Panel>("SelectionPillHost")!;
        PumpUntil(() => false, 0.2);          // loaded: the transitions are attached
        Assert.Equal(0, pill.Lift);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Playlists);
        var peak = 0.0;
        PumpUntil(() => { peak = Math.Max(peak, pill.Lift); return peak > 0.9; });
        Assert.True(peak > 0.9, $"the droplet only lifted to {peak}");

        PumpUntil(() => pill.Lift == 0);
        Assert.Equal(0, pill.Lift);
        Assert.False(pill.HasLens);
        var slot = view.FindControl<Panel>("ChromeHost")!.Bounds.Width / 4;
        PumpUntil(() => Math.Abs((host.RenderTransform?.Value.M31 ?? 0) - (2 * slot + 4)) < 0.5);
        Assert.Equal(2 * slot + 4, host.RenderTransform!.Value.M31, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void TheFloatingChrome_IsLightlyFrostedGlass_WithARefractingRim()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        window.UpdateLayout();

        var panels = new[] { "TabCapsule", "MiniBar", "SearchBubble" }.Select(n => view.FindControl<GlassPanel>(n)!).ToList();
        panels.AddRange(view.GetVisualDescendants().OfType<GlassPanel>().Where(p => p.Classes.Contains("header-glass")));
        Assert.True(panels.Count >= 4);
        foreach (var panel in panels)
        {
            Assert.True(panel.HasLens, $"{panel.Name}: no lens");
            Assert.True(panel.RefractionAmount >= panel.Refraction, $"{panel.Name}: the rim bends less than its band");
            Assert.True(panel.Specular > 0, $"{panel.Name}: no rim light");
            // The old frost (11) hid what passed beneath; Apple's barely blurs it.
            Assert.InRange(panel.BlurRadius, 1, 4);
        }
        window.Close();
    }

    /// <summary>Labels on the glass stay readable over any cover: dark glass caps how bright a
    /// white cover shows through, light glass lifts how dark a black one does.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheGlass_LimitsHowBrightOrDarkTheBackdropShows_PerTheme(bool light)
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
            var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
            using var rig = MobileFixtures.MakeRig(songs);
            var window = MobileFixtures.Mount(rig.Shell, out var view);
            rig.Shell.Player.PlayTracks(songs, 0);
            window.UpdateLayout();
            foreach (var name in new[] { "TabCapsule", "MiniBar", "SearchBubble" })
            {
                var panel = view.FindControl<GlassPanel>(name)!;
                if (light)
                {
                    Assert.True(panel.BackdropMinLuminance >= 0.2, $"{name}: black shows through at {panel.BackdropMinLuminance}");
                    Assert.Equal(1, panel.BackdropMaxLuminance);
                }
                else
                {
                    Assert.True(panel.BackdropMaxLuminance <= 0.5, $"{name}: white shows through at {panel.BackdropMaxLuminance}");
                    Assert.Equal(0, panel.BackdropMinLuminance);
                }
            }
            window.Close();
        }
        finally { app.RequestedThemeVariant = before; }
    }
}

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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
        var bar = view.FindControl<Panel>("ChromeHost")!.Bounds.Width;
        var target = ShellView.SelectionPillX(bar, 2);
        Assert.Equal(2 * bar / 5 - 2, target, 1);     // a little wider than its tab, centred on it
        PumpUntil(() => Math.Abs((host.RenderTransform?.Value.M31 ?? 0) - target) < 0.5);
        Assert.Equal(target, host.RenderTransform!.Value.M31, 1);
        window.Close();
    }

    /// <summary>Apple's selected-tab capsule overhangs its tab a little but stays inside the bar's
    /// rounded ends on the first and last tab - and is centred on its tab on every one (beta 2:
    /// the end tabs' capsule was clamped 6 dp off its icon and label).</summary>
    [Fact]
    public void TheSelectedTabCapsule_StaysInsideTheBarsEnds_CentredOnEveryTab()
    {
        const double bar = 380;
        var tab = bar / 5;
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(tab + 4, ShellView.SelectionPillWidth(bar, i), 3);
            Assert.Equal(i * tab - 2, ShellView.SelectionPillX(bar, i), 3);
        }
        Assert.Equal(4, ShellView.SelectionPillX(bar, 0));
        Assert.Equal(bar - ShellView.SelectionPillWidth(bar, 4) - 4, ShellView.SelectionPillX(bar, 4), 3);
        for (var i = 0; i < 5; i++)
        {
            var centre = ShellView.SelectionPillX(bar, i) + ShellView.SelectionPillWidth(bar, i) / 2;
            Assert.Equal((i + 0.5) * tab, centre, 3);
        }
    }

    /// <summary>The expanded bar: a full-width tab pill with the mini player's own capsule 8 dp above it.</summary>
    [AvaloniaFact]
    public void TheMiniPlayer_IsItsOwnCapsule_JustAboveTheTabs_AndFoldsIntoOneRow()
    {
        var songs = Enumerable.Range(0, 3).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        window.UpdateLayout();
        var tabs = view.FindControl<GlassPanel>("TabCapsule")!;
        var mini = view.FindControl<GlassPanel>("MiniBar")!;

        Assert.Equal(tabs.Bounds.Width, mini.Bounds.Width, 1);
        var gap = tabs.TranslatePoint(new Point(0, 0), view)!.Value.Y - mini.TranslatePoint(new Point(0, mini.Bounds.Height), view)!.Value.Y;
        Assert.Equal(8, gap, 1);
        Assert.Equal(62, tabs.Bounds.Height, 1);
        Assert.True(view.FindControl<Button>("MiniNextButton")!.IsEffectivelyEnabled);   // songs left to skip to

        rig.Shell.IsTabBarCollapsed = true;
        PumpUntil(() => Math.Abs(view.FindControl<GlassPanel>("TabCapsule")!.Bounds.Width - mini.Bounds.Height) < 0.5, 2);
        window.UpdateLayout();
        // One row: the current tab's circle, the mini player, the Search circle, all one height.
        var search = view.FindControl<GlassPanel>("SearchBubble")!;
        Assert.Equal(mini.Bounds.Height, tabs.Bounds.Height, 1);
        Assert.Equal(mini.Bounds.Height, search.Bounds.Height, 1);
        Assert.True(search.IsHitTestVisible);
        Assert.Equal(tabs.TranslatePoint(new Point(0, 0), view)!.Value.Y, mini.TranslatePoint(new Point(0, 0), view)!.Value.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void TheMiniPlayersNext_GreysOut_OnTheLastSong()
    {
        var songs = Enumerable.Range(0, 2).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 1);
        window.UpdateLayout();
        var next = view.FindControl<Button>("MiniNextButton")!;
        Assert.False(rig.Shell.Player.HasNext);
        Assert.False(next.IsEffectivelyEnabled);
        Assert.True(next.Classes.Contains("unavailable"));
        // Only the glyph dims: no grey disc from Fluent's disabled presenter behind it.
        var presenter = next.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        Assert.True(presenter.Background is null || presenter.Background is ISolidColorBrush { Color.A: 0 },
            $"Next has a {presenter.Background} disc while greyed");

        rig.Shell.Player.PlayNext(songs[0]);
        window.UpdateLayout();
        Assert.True(next.IsEffectivelyEnabled);
        Assert.False(next.Classes.Contains("unavailable"));
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

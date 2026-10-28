using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's buttons answer a press the way Apple's do: they dim and settle a touch smaller,
/// easing in and back out (opacity and scale transitions on the button), instead of jumping to a
/// lower opacity. Full-width rows only dim. Pills are full 44 dp touch targets, round-ended.
/// </summary>
public class MobileButtonStyleTests
{
    private static (MobileFixtures.Rig Rig, Window Window, ShellView View) Open()
    {
        var songs = new[] { MobileFixtures.Song("S0"), MobileFixtures.Song("S1") };
        var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        window.UpdateLayout();
        return (rig, window, view);
    }

    private static void Press(Button b, bool on) => ((IPseudoClasses)b.Classes).Set(":pressed", on);

    private static double Scale(Visual v) => v.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaFact]
    public void IconButton_PressDimsAndShrinks_ThroughEasedTransitions()
    {
        var (rig, window, view) = Open();
        using var _rig = rig;
        // Any icon-style button but the transport, which presses with Apple's own motion
        // (TransportPress_KeepsTheButtonStill below).
        var play = view.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && !b.Classes.Contains("tx")
                        && new[] { "icon", "round", "glass-round" }.Any(b.Classes.Contains));

        var transitions = play.Transitions;
        Assert.NotNull(transitions);
        Assert.Contains(transitions!, t => t is DoubleTransition d && d.Property == Visual.OpacityProperty && d.Easing != null);
        Assert.Contains(transitions!, t => t is TransformOperationsTransition r && r.Property == Visual.RenderTransformProperty && r.Easing != null);

        play.Transitions = null;   // read the targets, not a frame of the ease
        Press(play, true);
        window.UpdateLayout();
        Assert.InRange(play.Opacity, 0.5, 0.75);
        Assert.InRange(Scale(play), 0.94, 0.97);

        Press(play, false);
        window.UpdateLayout();
        Assert.Equal(1, play.Opacity);
        Assert.Equal(1, Scale(play));
        window.Close();
    }

    /// <summary>The mini player's Play and Next press with TransportGlyph's circle and dip
    /// (Apple Music's), so the button itself neither dims nor shrinks.</summary>
    [AvaloniaFact]
    public void TransportPress_KeepsTheButtonStill()
    {
        var (rig, window, view) = Open();
        using var _rig = rig;
        foreach (var name in new[] { "MiniPlayButton", "MiniNextButton" })
        {
            var b = MobileFixtures.Named<Button>(view, name);
            Assert.True(b.Classes.Contains("tx"), name);
            b.Transitions = null;
            Press(b, true);
            window.UpdateLayout();
            Assert.Equal(1, b.Opacity);
            Assert.Equal(1, Scale(b));
            Press(b, false);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void TabsAndPills_ShrinkOnPress_RowsOnlyDim()
    {
        var (rig, window, view) = Open();
        using var _rig = rig;
        var tab = MobileFixtures.Named<Button>(view, "FavoritesTab");
        var row = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row") && b.IsEffectivelyVisible);
        foreach (var b in new[] { tab, row })
        {
            b.Transitions = null;
            Press(b, true);
        }
        window.UpdateLayout();

        Assert.InRange(Scale(tab), 0.94, 0.97);
        Assert.True(tab.Opacity < 1);
        Assert.Equal(1, Scale(row));
        Assert.True(row.Opacity < 1);
        window.Close();
    }

    [AvaloniaFact]
    public void Pills_AreFullTouchTargets_WithRoundEnds()
    {
        // Library's own pills (Settings' were rows of Apple's grouped cards since 10-12): the
        // first-run card's, on a phone with no music yet.
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        window.UpdateLayout();

        var pills = view.GetVisualDescendants().OfType<Button>()
            .Where(b => (b.Classes.Contains("pill") || b.Classes.Contains("pill-accent")) && b.IsEffectivelyVisible)
            .ToList();
        Assert.NotEmpty(pills);
        Assert.All(pills, p =>
        {
            Assert.True(p.Bounds.Height >= 44, $"{p.Name} is {p.Bounds.Height} tall");
            Assert.Equal(p.Bounds.Height / 2, p.CornerRadius.TopLeft, 1);
        });
        window.Close();
    }
}

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Queue sheet: its header, the rows, the control pill's bindings, and the
/// slide up on open and down on close.</summary>
public class MobileQueueSheetTests
{
    private static Track[] Songs(int n, string prefix = "S") =>
        Enumerable.Range(0, n).Select(i => MobileFixtures.Song($"{prefix}{i}")).ToArray();

    private static (MobileFixtures.Rig Rig, Window Window, ShellView View, QueuePage Page, Track[] Songs) OpenQueue(
        string? source = "Alpha", double width = 412, double height = 915)
    {
        var songs = Songs(5);
        var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view, width, height);
        rig.Shell.Player.PlayTracks(songs, 0, source);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        window.UpdateLayout();
        var page = MobileFixtures.Find<QueuePage>(view);
        page.CompleteSlide();
        window.UpdateLayout();
        return (rig, window, view, page, songs);
    }

    /// <summary>Runs the headless clock in real time until <paramref name="done"/> or 3 s.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            System.Threading.Thread.Sleep(15);
        }
    }

    [AvaloniaFact]
    public void Open_SlidesTheSheetUpFromBelowTheScreen_AndFadesTheDimIn()
    {
        var songs = Songs(3);
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);   // the first open: the sheet joins the tree on this layout
        window.UpdateLayout();
        var page = MobileFixtures.Find<QueuePage>(view);
        var sheet = MobileFixtures.Named<Border>(page, "Sheet");
        var dim = MobileFixtures.Named<Border>(page, "Dim");
        double Offset() => sheet.RenderTransform?.Value.M32 ?? 0;

        Assert.True(Offset() >= window.Height - 1, $"starts at {Offset()}, not below the screen");
        var seen = new System.Collections.Generic.List<double>();
        PumpUntil(() =>
        {
            seen.Add(Offset());
            return Offset() == 0 && dim.Opacity == 1;
        });

        Assert.Equal(0, Offset());
        Assert.Equal(1, dim.Opacity);
        Assert.Contains(seen, y => y > 0 && y < window.Height - 1);    // passed through in between
        window.Close();
    }

    [AvaloniaFact]
    public void Header_ShowsTheTrack_WhereItPlaysFrom_AndItsPlace()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;

        Assert.Equal("S0", MobileFixtures.Named<TextBlock>(page, "NowTitle").Text);
        Assert.Equal("Artist", MobileFixtures.Named<TextBlock>(page, "NowArtist").Text);
        var source = MobileFixtures.Named<TextBlock>(page, "SourceLine");
        Assert.Equal("Playing from Alpha", source.Text);
        Assert.True(source.IsEffectivelyVisible);
        var position = MobileFixtures.Named<TextBlock>(page, "PositionLine");
        Assert.Equal("Track 1 of 5", position.Text);
        Assert.True(position.IsEffectivelyVisible);

        rig.Shell.Player.NextCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal("Track 2 of 5", position.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Header_HidesTheSourceLine_WhenTheSourceIsUnknown()
    {
        var (rig, window, _, page, _) = OpenQueue(source: null);
        using var _rig = rig;

        Assert.False(MobileFixtures.Named<TextBlock>(page, "SourceLine").IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<TextBlock>(page, "PositionLine").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Controls_AreBoundToTheShellAndPlayerCommands()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var shell = rig.Shell;
        var player = shell.Player;
        Button B(string name) => MobileFixtures.Named<Button>(page, name);

        Assert.Same(shell.ToggleQueueCommand, B("QueueGrabber").Command);
        Assert.Same(shell.ToggleQueueCommand, B("QueueCloseButton").Command);
        Assert.Same(shell.ToggleQueueCommand, B("QueueListButton").Command);
        Assert.Same(player.ClearUpNextCommand, B("ClearButton").Command);
        Assert.Same(player.CycleRepeatCommand, B("QueueRepeatButton").Command);
        Assert.Same(player.PreviousCommand, B("QueuePreviousButton").Command);
        Assert.Same(player.TogglePlayPauseCommand, B("QueuePlayPauseButton").Command);
        Assert.Same(player.NextCommand, B("QueueNextButton").Command);
        Assert.Same(player.ToggleShuffleCommand, B("QueueShuffleButton").Command);
        Assert.Same(shell.OpenCurrentTrackSheetCommand, B("QueueMoreButton").Command);

        B("QueueMoreButton").Command!.Execute(null);
        Assert.Same(player.CurrentTrack, shell.Sheet?.Track);
        window.Close();
    }

    [AvaloniaFact]
    public void Repeat_And_Shuffle_LightUpInTheAccent_WhenOn()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var repeat = MobileFixtures.Named<Button>(page, "QueueRepeatButton");
        var shuffle = MobileFixtures.Named<Button>(page, "QueueShuffleButton");
        Assert.False(repeat.Classes.Contains("on"));
        Assert.False(shuffle.Classes.Contains("on"));

        rig.Shell.Player.CycleRepeatCommand.Execute(null);
        rig.Shell.Player.ToggleShuffleCommand.Execute(null);
        Assert.True(repeat.Classes.Contains("on"));
        Assert.True(shuffle.Classes.Contains("on"));

        rig.Shell.Player.CycleRepeatCommand.Execute(null);                 // All → One
        Assert.True(repeat.Classes.Contains("on"));
        Assert.True(MobileFixtures.Named<PathIcon>(page, "RepeatOneGlyph").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Tapping_TheCloseButton_ClosesTheQueue()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var close = MobileFixtures.Named<Button>(page, "QueueCloseButton");
        var centre = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;

        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);

        Assert.False(rig.Shell.IsQueueOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Rows_HaveAReorderHandle_AndNoRemoveButton()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var row = MobileFixtures.Named<ItemsControl>(page, "QueueList").ContainerFromIndex(0)!;

        Assert.DoesNotContain(row.GetVisualDescendants().OfType<Button>(), b => b.Name == "RemoveButton");
        Assert.Contains(row.GetVisualDescendants().OfType<Border>(), b => b.Name == "DragHandle");
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Album");
        window.Close();
    }

    [AvaloniaFact]
    public void TheList_ScrollsUnderThePill_AndEndsAboveIt()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "QueueScroll");
        var pill = MobileFixtures.Named<GlassPanel>(page, "ControlPill");

        var scrollBottom = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), page)!.Value.Y;
        var pillTop = pill.TranslatePoint(new Point(0, 0), page)!.Value.Y;
        Assert.True(scrollBottom > pillTop, $"the list stops at {scrollBottom}, above the pill at {pillTop}");
        Assert.True(scroll.Padding.Bottom >= scrollBottom - pillTop, $"bottom padding {scroll.Padding.Bottom} < {scrollBottom - pillTop}");
        window.Close();
    }

    [AvaloniaFact]
    public void Landscape_PutsUpNextBesideTheCurrentTrack_AndThePillUnderIt()
    {
        var (rig, window, view, page, _) = OpenQueue(width: 915, height: 412);
        using var _rig = rig;
        view.ApplySafeArea(new Thickness(0, 24, 48, 0));   // status bar on top, 3-button navigation on the right
        window.UpdateLayout();
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "QueueScroll");
        var pill = MobileFixtures.Named<GlassPanel>(page, "ControlPill");
        var cover = MobileFixtures.Named<Border>(MobileFixtures.Named<Grid>(page, "NowRow"), "NowCover");
        double Left(Visual v) => v.TranslatePoint(new Point(0, 0), page)!.Value.X;
        double Top(Visual v) => v.TranslatePoint(new Point(0, 0), page)!.Value.Y;
        double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), page)!.Value.X;

        Assert.True(Left(scroll) >= Right(pill), $"the list at {Left(scroll)} overlaps the pill ending at {Right(pill)}");
        Assert.True(Left(scroll) >= Right(cover));
        Assert.True(Top(scroll) < Top(pill), "the list starts under the right column's header, not under the cover");
        Assert.True(scroll.Bounds.Height > 412 / 2.0, $"Up Next is only {scroll.Bounds.Height} tall");
        Assert.Equal(16, scroll.Padding.Bottom);                     // no pill under the list to clear
        Assert.True(Left(MobileFixtures.Named<Button>(page, "QueueCloseButton")) > Left(scroll), "✕ stays at the sheet's right edge");
        window.Close();
    }

    [AvaloniaFact]
    public void Close_SlidesDownBeforeHiding_TakesNoTouches_AndAReopenCancelsIt()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var sheet = MobileFixtures.Named<Border>(page, "Sheet");
        Assert.True(page.IsVisible);
        Assert.True(sheet.IsHitTestVisible);

        rig.Shell.ToggleQueueCommand.Execute(null);
        Assert.False(rig.Shell.IsQueueOpen);
        Assert.True(page.IsVisible);                // still on screen, sliding down
        Assert.True(page.IsClosing);
        Assert.False(sheet.IsHitTestVisible);

        rig.Shell.ToggleQueueCommand.Execute(null);  // reopened mid-slide
        Assert.True(page.IsVisible);
        Assert.False(page.IsClosing);
        Assert.True(sheet.IsHitTestVisible);

        rig.Shell.TryHandleBack();
        Assert.True(page.IsClosing);
        page.FinishClose();                          // what the close timer does
        Assert.False(page.IsVisible);
        Assert.False(page.IsClosing);
        window.Close();
    }

    [AvaloniaFact]
    public void Close_HidesThePage_OnceTheSlideHasRun()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;

        var sheet = MobileFixtures.Named<Border>(page, "Sheet");

        rig.Shell.ToggleQueueCommand.Execute(null);
        var lastSeen = 0.0;
        PumpUntil(() =>
        {
            if (page.IsVisible) lastSeen = sheet.RenderTransform?.Value.M32 ?? 0;
            return !page.IsVisible;
        });

        Assert.False(page.IsVisible);
        Assert.True(lastSeen > window.Height / 2, $"hid with the sheet at {lastSeen}, mid-slide");
        window.Close();
    }

    [AvaloniaFact]
    public void EqualizerBars_BounceOnlyWhilePlaying_AndWhileTheSheetIsUp()
    {
        var (rig, window, _, page, _) = OpenQueue();
        using var _rig = rig;
        var bars = MobileFixtures.Named<Panel>(page, "EqBars");
        Assert.True(bars.Classes.Contains("playing"));

        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.False(bars.Classes.Contains("playing"));
        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.True(bars.Classes.Contains("playing"));

        rig.Shell.ToggleQueueCommand.Execute(null);
        Assert.False(bars.Classes.Contains("playing"));   // closing: nothing to animate for
        page.FinishClose();
        Assert.False(bars.Classes.Contains("playing"));
        window.Close();
    }
}

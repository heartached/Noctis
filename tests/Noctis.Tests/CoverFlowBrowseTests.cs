using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #152 / #153 (enoversum): the Cover Flow carousel BROWSES the queue — wheel (one
/// card per notch), drag, arrow keys and a click on a side card move the row without
/// touching playback; a click on the centred card plays it; Repeat All makes the row run
/// round. The Collage gets the blurred backdrop, a marked playing tile, captions and
/// click-to-play tiles.
/// </summary>
public class CoverFlowBrowseTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    /// <summary>Queue t0..t5 started at t0 and advanced twice inside it: history [t1, t0]
    /// (both this queue's), playing t2, up next [t3, t4, t5].</summary>
    private static (PlayerViewModel player, Track[] t) Seed()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var t = Enumerable.Range(0, 6).Select(i => new Track { Title = $"t{i}", Artist = $"artist{i}", FilePath = $"t{i}.mp3" }).ToArray();
        player.ReplaceQueueAndPlay(t, 0);
        player.PlayFromUpNextAt(0);
        player.PlayFromUpNextAt(0);
        Assert.Same(t[2], player.CurrentTrack);
        Assert.Equal(new[] { t[1], t[0] }, player.History);
        Assert.Equal(new[] { t[3], t[4], t[5] }, player.UpNext);
        Assert.Equal(2, player.PlayedInThisQueueCount);
        return (player, t);
    }

    // ── View model ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void BrowseBy_MovesTheRowNotThePlayback_AndMarksThePlayingCard()
    {
        var (player, t) = Seed();
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        var steps = new List<int>();
        vm.CarouselShifted += (_, s) => steps.Add(s);

        Assert.Same(t[2], vm.FocusedSlot.Track);
        Assert.True(vm.FocusedSlot.IsPlaying);
        Assert.False(vm.Row.Any(s => s.IsMarked));

        Assert.True(vm.BrowseBy(2));
        Assert.Same(t[2], player.CurrentTrack);             // nothing played
        Assert.Equal(2, vm.BrowseOffset);
        Assert.Same(t[4], vm.FocusedSlot.Track);
        Assert.False(vm.FocusedSlot.IsPlaying);
        var playing = vm.Row[CoverFlowViewModel.RowSideSlots - 2];
        Assert.Same(t[2], playing.Track);
        Assert.True(playing.IsMarked);
        Assert.Equal(new[] { 2 }, steps);

        // The far end stops the row (no Repeat All): only one more card to the right.
        Assert.True(vm.BrowseBy(5));
        Assert.Equal(3, vm.BrowseOffset);
        Assert.Equal(1, steps[^1]);
        Assert.False(vm.BrowseBy(1));
        Assert.Null(vm.Row[CoverFlowViewModel.RowSideSlots + 1].Track);

        // And the near end: two history cards to the left of the playing one.
        Assert.True(vm.BrowseBy(-10));
        Assert.Equal(-2, vm.BrowseOffset);
        Assert.Same(t[0], vm.FocusedSlot.Track);
        Assert.Same(t[2], player.CurrentTrack);
    }

    [AvaloniaFact]
    public void PlayFocused_PlaysTheBrowsedCard_AndTheRowStaysPut()
    {
        var (player, t) = Seed();
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        vm.BrowseBy(2);
        var steps = new List<int>();
        vm.CarouselShifted += (_, s) => steps.Add(s);

        vm.PlayFocusedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(t[4], player.CurrentTrack);
        Assert.Equal(0, vm.BrowseOffset);
        Assert.Same(t[4], vm.FocusedSlot.Track);
        Assert.True(vm.FocusedSlot.IsPlaying);
        Assert.Empty(steps); // the centred card did not move: no slide

        // Browsed into history: plays it and re-queues what came after.
        vm.BrowseBy(-1);
        vm.PlayFocusedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(player.CurrentTrack, vm.FocusedSlot.Track);
        Assert.Equal(0, vm.BrowseOffset);
    }

    [AvaloniaFact]
    public void ATrackChangeWhileBrowsing_KeepsTheBrowsedCardCentred()
    {
        var (player, t) = Seed();
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        vm.BrowseBy(3); // t5
        var steps = new List<int>();
        vm.CarouselShifted += (_, s) => steps.Add(s);

        player.PlayFromUpNextAt(0); // t3 plays (as a natural advance would)
        Dispatcher.UIThread.RunJobs();

        Assert.Same(t[5], vm.FocusedSlot.Track);
        Assert.Equal(2, vm.BrowseOffset);
        Assert.Same(t[3], vm.Row[CoverFlowViewModel.RowSideSlots - 2].Track);
        Assert.True(vm.Row[CoverFlowViewModel.RowSideSlots - 2].IsMarked);
        Assert.Empty(steps);

        vm.ReturnToPlayingCommand.Execute(null);
        Assert.Equal(0, vm.BrowseOffset);
        Assert.Same(t[3], vm.FocusedSlot.Track);
        Assert.Equal(new[] { -2 }, steps);
    }

    [AvaloniaFact]
    public void RepeatAll_RunsRound_PastTheLastTrackToTheFirstOfThisQueue()
    {
        var (player, t) = Seed();
        player.RepeatMode = RepeatMode.All;
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        Dispatcher.UIThread.RunJobs();

        // Ring t0..t5, playing t2: three to the right, two to the left, each track once.
        var shown = vm.Row.Where(s => s.Track != null).Select(s => s.Track!).ToList();
        Assert.Equal(6, shown.Count);
        Assert.Equal(6, shown.Distinct().Count());

        vm.BrowseBy(3);                       // t5, the last up-next track
        Assert.Same(t[5], vm.FocusedSlot.Track);
        Assert.True(vm.BrowseBy(1));          // runs round to t0
        Assert.Same(t[0], vm.FocusedSlot.Track);
        Assert.Equal(-2, vm.BrowseOffset);
        Assert.Same(t[1], vm.Row[CoverFlowViewModel.RowSideSlots + 1].Track);
        Assert.Same(t[5], vm.Row[CoverFlowViewModel.RowSideSlots - 1].Track);
        Assert.True(vm.BrowseBy(-1));         // and back
        Assert.Same(t[5], vm.FocusedSlot.Track);
        Assert.Same(t[2], player.CurrentTrack);

        // Repeat off: the row stops at the end again.
        player.RepeatMode = RepeatMode.Off;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.BrowseBy(1));
        Assert.Same(t[5], vm.FocusedSlot.Track);
    }

    [Fact]
    public void Fold_WrapsOntoTheRing()
    {
        // played 2, length 6 → positions −2..3
        Assert.Equal(-2, CoverFlowViewModel.Fold(4, 2, 6));
        Assert.Equal(3, CoverFlowViewModel.Fold(-3, 2, 6));
        Assert.Equal(0, CoverFlowViewModel.Fold(6, 2, 6));
        Assert.Equal(1, CoverFlowViewModel.Fold(1, 2, 6));
    }

    [AvaloniaFact]
    public void SwitchingLayout_BringsThePlayingTrackBackToTheCentre()
    {
        var (player, t) = Seed();
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        vm.BrowseBy(2);
        vm.Layout = CoverFlowLayout.Cascade;
        Assert.Equal(0, vm.BrowseOffset);
        Assert.Same(t[2], vm.FocusedSlot.Track);
    }

    // ── View ──────────────────────────────────────────────────────────────────

    private static (CoverFlowView view, CoverFlowViewModel vm, Window window, PlayerViewModel player, Track[] t) Open()
    {
        EnsureAppStyles();
        var (player, t) = Seed();
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        var view = new CoverFlowView { DataContext = vm };
        var window = new Window { Width = 1600, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (view, vm, window, player, t);
    }

    private static Point ArtworkCentre(CoverFlowCard card, Window window)
    {
        var art = card.FindControl<Panel>("ArtworkPanel")!;
        return art.TranslatePoint(new Point(art.Bounds.Width / 2, art.Bounds.Height / 2), window)!.Value;
    }

    private static void Settle(CoverFlowView view)
    {
        Dispatcher.UIThread.RunJobs();
        view.FinishSlide();
        TopLevel.GetTopLevel(view)!.UpdateLayout();
    }

    [AvaloniaFact]
    public void Wheel_OneCardPerNotch_HighResolutionDeltasAddUp_AndNothingPlays()
    {
        var (view, vm, window, player, t) = Open();
        var at = ArtworkCentre(view.FindControl<CoverFlowCard>("CenterCard")!, window);

        window.MouseWheel(at, new Vector(0, -1));
        Assert.Equal(1, vm.BrowseOffset);

        // A high-resolution wheel reports one notch as several small events: still one card.
        for (var i = 0; i < 4; i++) window.MouseWheel(at, new Vector(0, -0.25));
        Assert.Equal(2, vm.BrowseOffset);

        window.MouseWheel(at, new Vector(0, 1));
        Assert.Equal(1, vm.BrowseOffset);
        Assert.Same(t[2], player.CurrentTrack);
        Assert.Same(t[3], vm.FocusedSlot.Track);
    }

    [AvaloniaFact]
    public void ClickingASideCard_CentresIt_ClickingTheCentredCard_PlaysIt()
    {
        var (view, vm, window, player, t) = Open();
        var next2 = view.FindControl<CoverFlowCard>("SlotNext2")!;
        var p = ArtworkCentre(next2, window);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Assert.Equal(2, vm.BrowseOffset);
        Assert.Same(t[2], player.CurrentTrack);
        Assert.True(view.FindControl<CoverFlowCard>("SlotPrev2")!.IsPlaying); // the mark

        Settle(view);
        var centre = view.FindControl<CoverFlowCard>("CenterCard")!;
        Assert.Equal(t[4].Title, centre.Title);
        var c = ArtworkCentre(centre, window);
        window.MouseDown(c, MouseButton.Left);
        window.MouseUp(c, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(t[4], player.CurrentTrack);
        Assert.Equal(0, vm.BrowseOffset);
        Assert.False(view.FindControl<CoverFlowCard>("SlotPrev2")!.IsPlaying);
    }

    [AvaloniaFact]
    public void Dragging_BrowsesOneCardPerCardWidth_WithoutPlaying()
    {
        var (view, vm, window, player, t) = Open();
        var start = ArtworkCentre(view.FindControl<CoverFlowCard>("CenterCard")!, window);
        window.MouseMove(start);
        window.MouseDown(start, MouseButton.Left);
        var travel = CoverFlowView.DragStepPixels * 2 + 10;
        for (var i = 1; i <= 10; i++)
            window.MouseMove(start - new Point(travel * i / 10, 0));
        window.MouseUp(start - new Point(travel, 0), MouseButton.Left);

        Assert.Equal(2, vm.BrowseOffset);       // dragged left: up-next cards came in
        Assert.Same(t[2], player.CurrentTrack); // and the release was not a click
    }

    [AvaloniaFact]
    public void ArrowKeysBrowse_EnterPlays_EscapeReturns()
    {
        var (view, vm, window, player, t) = Open();
        view.Focus();
        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Assert.Equal(1, vm.BrowseOffset);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal(0, vm.BrowseOffset);
        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        Assert.Same(t[1], vm.FocusedSlot.Track);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(t[1], player.CurrentTrack);
    }

    [AvaloniaFact]
    public void SlideInterruptedMidway_CarriesOnFromWhereTheCardsAre()
    {
        var (view, _, _, _, _) = Open();
        view.OnCarouselShifted(null, 1);
        view.ApplySlideFrame(0.5);
        Assert.Equal(0.5, view.PositionOf(0), 2);
        // Another step lands: the new centre card was drawn 1 + 0.5 slots out.
        view.OnCarouselShifted(null, 1);
        view.ApplySlideFrame(0);
        Assert.Equal(1.5, view.PositionOf(0), 2);
        view.ApplySlideFrame(1);
        Assert.Equal(0, view.PositionOf(0), 2);
    }

    [AvaloniaFact]
    public void Collage_TilesShowTheirSong_AndAClickPlaysIt()
    {
        var (view, vm, window, player, t) = Open();
        vm.Layout = CoverFlowLayout.Collage;
        view.FinishLayoutMorph();
        window.UpdateLayout();

        var collage = view.FindControl<Viewbox>("CollageViewbox")!;
        Assert.True(collage.IsHitTestVisible);
        var tiles = collage.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("collage-tile")).ToList();
        Assert.Equal(31, tiles.Count);
        var tile2 = tiles.Single(b => (string?)b.Tag == "2");
        Assert.True(tile2.IsVisible);
        Assert.Same(t[4], tile2.Content);
        var texts = tile2.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("t4", texts);
        Assert.Contains("artist4", texts);

        // Hearts keep the per-column sizes the hand-written tiles had (fill every tile first).
        player.AddRangeToQueue(Enumerable.Range(0, 30).Select(i => new Track { Title = $"x{i}", FilePath = $"x{i}.mp3" }).ToList(),
            announce: false);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        double HeartSize(string tag) => tiles.Single(b => (string?)b.Tag == tag)
            .GetVisualDescendants().OfType<HeartIcon>().Single().Size;
        Assert.Equal(18, HeartSize("1"));
        Assert.Equal(16, HeartSize("2"));
        Assert.Equal(14, HeartSize("8"));
        Assert.Equal(12, HeartSize("14"));
        Assert.Equal(11, HeartSize("22"));

        // The playing tile names its song, always.
        var centre = view.FindControl<Border>("CollageCenterCover")!;
        var centreTexts = centre.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("t2", centreTexts);

        var p = tile2.TranslatePoint(new Point(tile2.Bounds.Width / 2, tile2.Bounds.Height / 2), window)!.Value;
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(t[4], player.CurrentTrack);
    }
}

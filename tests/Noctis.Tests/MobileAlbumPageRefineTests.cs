using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The album page after the owner's Apple Music references (2026-10-01): a compact centred
/// shuffle / Play / heart group, the explicit badge right after the title with the title close
/// to its number, Apple's footer (quality, date, songs and minutes, ℗, record label), the
/// desktop's playing bars, the scrolled header with its small title, and the description
/// paragraph with MORE / LESS.
/// </summary>
public class MobileAlbumPageRefineTests
{
    private static (Album Album, Track[] Tracks) Sample(int count = 4, Action<Track, int>? shape = null)
    {
        var tracks = Enumerable.Range(1, count).Select(i =>
        {
            var t = MobileFixtures.Song($"Song {i}");
            t.TrackNumber = i;
            shape?.Invoke(t, i);
            return t;
        }).ToArray();
        var album = MobileFixtures.MakeAlbum("An Evening with Silk Sonic", "Silk Sonic", tracks);
        album.Year = 2021;
        return (album, tracks);
    }

    private sealed class FakeDescriptions(string? text) : IAlbumDescriptionSource
    {
        public CancellationToken Token;
        public List<(string Artist, string Album)> Asked { get; } = new();
        public TaskCompletionSource<string?>? Pending;

        public Task<string?> GetDescriptionAsync(string artist, string album, CancellationToken ct)
        {
            Asked.Add((artist, album));
            Token = ct;
            return Pending?.Task ?? Task.FromResult(text);
        }
    }

    private static MobileFixtures.Rig Rig(Track[] tracks, Album album, IAlbumDescriptionSource? descriptions = null)
    {
        var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        if (descriptions == null) return rig;
        // AlbumDescriptions is init-only: rebuild the shell around the same fakes with it set.
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            TintFactory = rig.Shell.TintFactory,
            AlbumDescriptions = descriptions,
        };
        return new MobileFixtures.Rig
        {
            Shell = shell, Library = rig.Library, Player = rig.Player, Persistence = rig.Persistence,
            History = rig.History, Root = rig.Root,
        };
    }

    private static Rect BoundsIn(Visual control, Visual root) =>
        new(control.TranslatePoint(new Point(0, 0), root)!.Value, control.Bounds.Size);

    private static void Pump(int frames = 40)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);   // the frame clock is the wall clock
        }
    }

    // ---- 1. Buttons -------------------------------------------------------------------------

    /// <summary>Apple's row: a ~148 × 46 Play pill with 46 circles 14 apart either side, the
    /// group centred, not pinned to the screen edges.</summary>
    [AvaloniaFact]
    public void ButtonRow_IsATightCentredGroup()
    {
        var (album, tracks) = Sample();
        using var rig = Rig(tracks, album);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var shuffle = BoundsIn(MobileFixtures.Named<Button>(page, "ShuffleButton"), view);
        var play = BoundsIn(MobileFixtures.Named<Button>(page, "PlayButton"), view);
        var heart = BoundsIn(MobileFixtures.Named<Button>(page, "FavouriteButton"), view);

        Assert.InRange(play.Width, 140, 150);
        Assert.InRange(play.Height, 44, 48);
        Assert.Equal(play.Height, shuffle.Height, 1);
        Assert.Equal(shuffle.Width, shuffle.Height, 1);
        Assert.Equal(heart.Size, shuffle.Size);
        Assert.InRange(play.Left - shuffle.Right, 12, 16);
        Assert.InRange(heart.Left - play.Right, 12, 16);
        Assert.Equal(view.Bounds.Width / 2, (shuffle.Left + heart.Right) / 2, 1);
        Assert.Equal(play.Center.Y, shuffle.Center.Y, 1);
        window.Close();
    }

    // ---- 2. Rows ----------------------------------------------------------------------------

    /// <summary>The badge sits right after a short title (not at the row's end), a long title
    /// trims before it, and the title starts just after a ~30 dp number column.</summary>
    [AvaloniaFact]
    public void ExplicitBadge_FollowsTheTitle_AndTheTitleSitsCloseToItsNumber()
    {
        var (album, tracks) = Sample(2, (t, i) =>
        {
            t.IsExplicit = true;
            t.Title = i == 1 ? "777" : "After Last Night (with Thundercat & Bootsy Collins) and a much longer tail";
        });
        using var rig = Rig(tracks, album);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var list = MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<AlbumPage>(view), "TrackList");
        T In<T>(int row, string name) where T : Control =>
            list.ContainerFromIndex(row)!.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        var shortTitle = BoundsIn(In<TextBlock>(0, "RowTitle"), view);
        var shortBadge = BoundsIn(In<Border>(0, "RowExplicit"), view);
        Assert.True(In<Border>(0, "RowExplicit").Classes.Contains("explicit-badge"));   // the desktop's badge
        Assert.True(In<Border>(0, "RowExplicit").Classes.Contains("compact"));
        Assert.Equal(shortTitle.Right + 6, shortBadge.Left, 1);
        Assert.True(shortBadge.Right < view.Bounds.Width / 2);                         // not at the row's end

        var number = BoundsIn(In<TextBlock>(0, "RowNumber"), view);
        Assert.InRange(shortTitle.Left, 44, 52);                                       // 14 + 30 + 4
        Assert.InRange(shortTitle.Left - number.Right, 0, 24);
        Assert.InRange(number.Center.X, 14 + 15 - 1, 14 + 15 + 1);                     // centred in its column

        var longTitle = In<TextBlock>(1, "RowTitle");
        var longBadge = BoundsIn(In<Border>(1, "RowExplicit"), view);
        var more = BoundsIn(list.ContainerFromIndex(1)!.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("icon")), view);
        Assert.True(longBadge.Right <= more.Left, $"badge {longBadge} runs into … {more}");
        Assert.Equal(BoundsIn(longTitle, view).Right + 6, longBadge.Left, 1);
        Assert.True(longTitle.TextLayout.TextLines[0].HasCollapsed);   // trimmed with an ellipsis
        window.Close();
    }

    /// <summary>The playing row's bars are the desktop indicator's: five 1.75-wide bars 1.75
    /// apart in a 12-high row, rounded by half their width, the same sine heights, frequencies,
    /// phases and pause flatten (read off EqVisualizer and EqBar themselves).</summary>
    [Fact]
    public void PlayingBars_MatchTheDesktopRowIndicator()
    {
        var desktop = typeof(Noctis.Controls.EqVisualizer);
        T Const<T>(string name) => (T)desktop.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(Const<double>("BarMin"), PlayingBars.BarMin);
        Assert.Equal(Const<double>("BarMax"), PlayingBars.BarMax);
        Assert.Equal(Const<double>("FlatHeight"), PlayingBars.FlatHeight);
        Assert.Equal(Const<double[]>("Phases"), PlayingBars.Phases);
        Assert.Equal(Const<double[]>("Frequencies"), PlayingBars.Frequencies);
        Assert.Equal(Const<TimeSpan>("FlattenDuration"), PlayingBars.FlattenDuration);
        Assert.Equal(Noctis.Controls.EqVisualizer.HiddenPollInterval, PlayingBars.HiddenPollInterval);
        Assert.Equal(Noctis.Controls.EqBar.Radius, PlayingBars.Radius);
        Assert.Equal(5, PlayingBars.BarCount);

        // The no-tap motion: a sine per bar between BarMin and BarMax.
        Assert.Equal(PlayingBars.BarMin + (PlayingBars.BarMax - PlayingBars.BarMin) * 0.5, PlayingBars.OscillationHeight(0, 0), 6);
        for (var t = 0.0; t < 2; t += 0.07)
            for (var i = 0; i < 5; i++)
                Assert.InRange(PlayingBars.OscillationHeight(t, i), PlayingBars.BarMin, PlayingBars.BarMax);

        // Laid out like the desktop's StackPanel of EqBars: 1.75 + 1.75 per bar, centred in 12.
        var r = PlayingBars.BarRect(2, 6, scale: 0);
        Assert.Equal(new Rect(7, 3, 1.75, 6), r);
        Assert.Equal(new Rect(0, 0, 1.75, 12), PlayingBars.BarRect(0, 30, scale: 0));   // capped at the row
    }

    /// <summary>The bars move on the frame clock while the row plays, ease flat on pause and
    /// then ask for no more frames, and stop when the page goes (a Render-priority
    /// DispatcherTimer pinned the Android UI thread, 2026-10-01).</summary>
    [AvaloniaFact]
    public void PlayingBars_MoveOnTheFrameClock_AndFlattenOnPause()
    {
        var (album, tracks) = Sample();
        using var rig = Rig(tracks, album);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        vm.PlayTrackCommand.Execute(vm.Tracks[1]);
        window.UpdateLayout();

        var bars = MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<AlbumPage>(view), "TrackList")
            .ContainerFromIndex(1)!.GetVisualDescendants().OfType<PlayingBars>().First();
        Pump(12);
        var moving = bars.BarHeights.ToArray();
        Assert.Contains(moving, h => h > PlayingBars.FlatHeight);
        Pump(6);
        Assert.NotEqual(moving, bars.BarHeights.ToArray());

        Assert.True(bars.IsRunning);

        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Pump(40);   // 420 ms flatten
        Assert.All(bars.BarHeights, h => Assert.Equal(PlayingBars.FlatHeight, h, 3));
        Assert.False(bars.IsRunning);   // paused: no frame asked for, nothing keeps running

        // Playing again, then the page goes: the bars stop with it.
        rig.Shell.Player.TogglePlayPauseCommand.Execute(null);
        Pump(4);
        Assert.True(bars.IsRunning);
        rig.Shell.NavigateBackCommand.Execute(null);
        window.UpdateLayout();
        Pump(4);
        Assert.False(bars.IsRunning);
        window.Close();
    }

    // ---- 3. Footer --------------------------------------------------------------------------

    [AvaloniaFact]
    public void Footer_IsApplesBlock_FromTheAlbumsTags()
    {
        var (album, tracks) = Sample(10, (t, i) =>
        {
            t.Codec = "FLAC";
            t.BitsPerSample = 24;
            t.SampleRate = 96000;
            t.Duration = TimeSpan.FromSeconds(218.6);
            t.ReleaseDate = i == 1 ? "" : "2021-11-12";
            t.Copyright = i < 3 ? "" : "℗ 2021 Aftermath Entertainment and Atlantic Recording Corporation";
            t.Label = i < 5 ? "" : "Aftermath Entertainment";
        });
        album.TotalDuration = TimeSpan.FromSeconds(tracks.Sum(t => t.Duration.TotalSeconds));   // 36m 26s
        using var rig = Rig(tracks, album);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        string Text(string name) => MobileFixtures.Named<TextBlock>(page, name).Text!;
        bool Shown(string name) => MobileFixtures.Named<Control>(page, name).IsVisible;
        Assert.Equal("Hi-Res Lossless · 24-bit/96 kHz FLAC", Text("FooterQualityText"));
        Assert.Equal("November 12, 2021", Text("FooterDate"));
        Assert.Equal("10 songs, 36 minutes", Text("FooterSongs"));
        Assert.Equal("℗ 2021 Aftermath Entertainment and Atlantic Recording Corporation", Text("FooterCopyright"));
        Assert.Equal("Aftermath Entertainment", Text("FooterLabelName"));
        Assert.True(Shown("FooterQuality") && Shown("FooterDate") && Shown("FooterCopyright") && Shown("FooterLabel"));
        window.Close();
    }

    /// <summary>A sparsely tagged album: the year for the date, and no quality, ℗ or label lines.</summary>
    [AvaloniaFact]
    public void Footer_HidesWhatTheTagsDoNotGive()
    {
        var (album, tracks) = Sample(1);
        using var rig = Rig(tracks, album);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        Assert.Equal("2021", MobileFixtures.Named<TextBlock>(page, "FooterDate").Text);
        Assert.Equal("1 song, 1 minute", MobileFixtures.Named<TextBlock>(page, "FooterSongs").Text);
        Assert.False(MobileFixtures.Named<StackPanel>(page, "FooterQuality").IsVisible);
        Assert.False(MobileFixtures.Named<TextBlock>(page, "FooterCopyright").IsVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(page, "FooterLabel").IsVisible);
        window.Close();
    }

    [Theory]
    [InlineData(1, 59, "1 song, 0 minutes")]
    [InlineData(15, 56 * 60 + 40, "15 songs, 56 minutes")]
    [InlineData(21, 3600 + 61, "21 songs, 1 hour, 1 minute")]
    [InlineData(30, 2 * 3600 + 12 * 60, "30 songs, 2 hours, 12 minutes")]
    [InlineData(30, 2 * 3600 + 20, "30 songs, 2 hours")]
    public void SongsLine_IsApplesWording(int songs, int seconds, string expected)
    {
        var album = new Album { TrackCount = songs, TotalDuration = TimeSpan.FromSeconds(seconds) };
        Assert.Equal(expected, AlbumPageViewModel.BuildSongsLine(album));
    }

    // ---- 5. Scrolled header -----------------------------------------------------------------

    /// <summary>At rest the big title shows and the small one does not; scrolled past the
    /// buttons, the big title has faded and the small centred one shows between ‹ and ….</summary>
    [AvaloniaFact]
    public void SmallTitle_FadesInAsTheBigTitlePassesUnderTheButtons()
    {
        var (album, tracks) = Sample(40);
        using var rig = Rig(tracks, album);
        rig.Shell.SafeArea = new Thickness(0, 40, 0, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var big = MobileFixtures.Named<TextBlock>(page, "AlbumTitle");
        var small = MobileFixtures.Named<TextBlock>(page, "SmallTitle");
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "AlbumScroll");
        Assert.Equal(1, big.Opacity);
        Assert.Equal(0, small.Opacity);

        // The title's top meets the buttons' bottom (40 + 52): the scrim is in, the title still whole.
        var barBottom = 40 + AlbumPage.TopBarHeight;
        scroll.Offset = new Vector(0, big.TranslatePoint(new Point(0, 0), view)!.Value.Y - barBottom);
        window.UpdateLayout();
        Assert.Equal(1, MobileFixtures.Named<Border>(page, "TopScrim").Opacity, 2);
        Assert.Equal(1, big.Opacity, 2);
        Assert.Equal(0, small.Opacity, 2);

        scroll.Offset = new Vector(0, scroll.Offset.Y + big.Bounds.Height + 4);
        window.UpdateLayout();
        Assert.Equal(0, big.Opacity, 2);
        Assert.Equal(1, small.Opacity, 2);
        var smallBox = BoundsIn(small, view);
        Assert.Equal(view.Bounds.Width / 2, smallBox.Center.X, 1);
        var back = BoundsIn(MobileFixtures.Named<Button>(page, "BackButton"), view);
        Assert.InRange(smallBox.Center.Y, back.Center.Y - 1, back.Center.Y + 1);
        Assert.True(smallBox.Left >= back.Right);
        window.Close();
    }

    // ---- 6. Description ---------------------------------------------------------------------

    private const string LongText =
        "Silk Sonic is an American musical superduo consisting of Bruno Mars and Anderson .Paak. " +
        "Their debut album was released in November 2021, preceded by the singles Leave the Door Open, " +
        "Skate and Smokin Out the Window, and it went on to win four Grammy Awards including Record of the Year. " +
        "The record is a love letter to 1970s soul, recorded with Bootsy Collins as the host of the evening.";

    [AvaloniaFact]
    public void Description_IsHidden_WithoutASource_OrWhenItHasNone()
    {
        foreach (var source in new IAlbumDescriptionSource?[] { null, new FakeDescriptions(null) })
        {
            var (album, tracks) = Sample();
            using var rig = Rig(tracks, album, source);
            var window = MobileFixtures.Mount(rig.Shell, out var view);
            rig.Shell.OpenAlbumCommand.Execute(album);
            window.UpdateLayout();
            var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
            Assert.Null(vm.Description);
            Assert.False(MobileFixtures.Named<Button>(MobileFixtures.Find<AlbumPage>(view), "DescriptionBlock").IsVisible);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Description_IsAskedOnceByArtistAndAlbum_AndCancelledWhenThePageCloses()
    {
        var source = new FakeDescriptions(null) { Pending = new TaskCompletionSource<string?>() };
        var (album, tracks) = Sample();
        using var rig = Rig(tracks, album, source);
        rig.Shell.OpenAlbumsCommand.Execute(null);
        rig.Shell.OpenAlbumCommand.Execute(album);
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal(new[] { ("Silk Sonic", "An Evening with Silk Sonic") }, source.Asked);
        Assert.False(source.Token.IsCancellationRequested);

        rig.Shell.NavigateBackCommand.Execute(null);
        Assert.True(source.Token.IsCancellationRequested);
        source.Pending.SetResult(LongText);   // lands after the close: dropped
        await vm.DescriptionLoad;
        Assert.Null(vm.Description);
    }

    /// <summary>Folded: three lines with MORE over the end of the last; a tap eases the clip
    /// open to the whole text with LESS under it, another folds it back.</summary>
    [AvaloniaFact]
    public void Description_FoldsToThreeLines_AndUnfoldsInPlace()
    {
        var (album, tracks) = Sample();
        using var rig = Rig(tracks, album, new FakeDescriptions(LongText));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var vm = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        var block = MobileFixtures.Named<Button>(page, "DescriptionBlock");
        var clip = MobileFixtures.Named<Border>(page, "DescriptionClip");
        var text = MobileFixtures.Named<TextBlock>(page, "DescriptionText");
        var more = MobileFixtures.Named<Border>(page, "DescriptionMore");
        var less = MobileFixtures.Named<TextBlock>(page, "DescriptionLess");
        Assert.True(block.IsVisible);
        Assert.Equal(LongText, text.Text);
        Assert.True(text.Bounds.Height > 3 * 20, $"sample too short to fold: {text.Bounds.Height}");
        Assert.Equal(3 * 20, clip.Bounds.Height, 1);
        Assert.True(more.IsVisible);
        Assert.Equal(1, more.Opacity);
        Assert.Equal(0, less.Opacity);

        block.Command!.Execute(null);
        Assert.True(vm.IsDescriptionExpanded);
        Pump();
        window.UpdateLayout();
        Assert.Equal(text.Bounds.Height + less.Bounds.Height + less.Margin.Top, clip.Bounds.Height, 1);
        Assert.False(more.IsVisible);
        Assert.Equal(1, less.Opacity);

        block.Command!.Execute(null);
        Assert.False(vm.IsDescriptionExpanded);
        Pump();
        window.UpdateLayout();
        Assert.Equal(3 * 20, clip.Bounds.Height, 1);
        Assert.True(more.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ShortDescription_ShowsWhole_WithoutMoreOrLess()
    {
        var (album, tracks) = Sample();
        using var rig = Rig(tracks, album, new FakeDescriptions("A short note."));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        window.UpdateLayout();
        window.UpdateLayout();

        var page = MobileFixtures.Find<AlbumPage>(view);
        var text = MobileFixtures.Named<TextBlock>(page, "DescriptionText");
        Assert.Equal(text.Bounds.Height, MobileFixtures.Named<Border>(page, "DescriptionClip").Bounds.Height, 1);
        Assert.False(MobileFixtures.Named<Border>(page, "DescriptionMore").IsVisible);
        Assert.Equal(0, MobileFixtures.Named<TextBlock>(page, "DescriptionLess").Opacity);
        window.Close();
    }
}

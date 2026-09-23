using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Home tab's recent rows, from the persisted play log.</summary>
public class MobileHomeTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    [Fact]
    public void LastPlayed_IsTheNewestDistinctSongs_Numbered()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        var c = MobileFixtures.Song("C");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, log: h =>
        {
            h.Seed(a, Now.AddHours(-4));
            h.Seed(b, Now.AddHours(-3));
            h.Seed(a, Now.AddHours(-2));
            h.Seed(c, Now.AddHours(-1));
        });

        var home = rig.Shell.Home;
        Assert.Equal(new[] { "C", "A", "B" }, home.LastPlayed.Select(r => r.Track.Title));
        Assert.Equal(new[] { 1, 2, 3 }, home.LastPlayed.Select(r => r.Number));
        Assert.False(home.IsEmpty);
    }

    [Fact]
    public void RecentlyPlayedAlbums_AndRecentlyAdded_FillTheRails()
    {
        var a = MobileFixtures.Song("A", daysAgo: 9);
        var b = MobileFixtures.Song("B", daysAgo: 1);
        var alpha = MobileFixtures.MakeAlbum("Alpha", "X", a);
        var beta = MobileFixtures.MakeAlbum("Beta", "Y", b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { alpha, beta }, log: h =>
        {
            h.Seed(b, Now.AddHours(-2));
            h.Seed(a, Now.AddHours(-1));
        });

        Assert.Equal(new[] { "Alpha", "Beta" }, rig.Shell.Home.RecentlyPlayedAlbums.Select(x => x.Name));
        Assert.Equal(new[] { "Beta", "Alpha" }, rig.Shell.Home.RecentlyAddedAlbums.Select(x => x.Name));
    }

    /// <summary>Review Focus #1 (Home half): nothing played, nothing scanned.</summary>
    [AvaloniaFact]
    public void Home_EmptyLog_ShowsTheEmptyState()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
        window.UpdateLayout();

        Assert.True(rig.Shell.Home.IsEmpty);
        var page = MobileFixtures.Find<HomePage>(view);
        Assert.True(page.IsVisible);
        Assert.True(MobileFixtures.Named<Border>(page, "HomeEmpty").IsVisible);
        Assert.False(MobileFixtures.Named<ItemsControl>(page, "LastPlayedList").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void HomeTab_IsShown_AndAPlayLandsOnLastPlayed()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(view.FindControl<Button>("HomeTab")!.IsVisible);

        rig.Shell.Player.PlayTracks(new[] { a, b }, 1);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));   // its audio started: now it is a play
        rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
        window.UpdateLayout();

        Assert.Equal("B", rig.Shell.Home.LastPlayed[0].Track.Title);
        Assert.Contains(MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<HomePage>(view), "LastPlayedList")
            .GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "B");
        window.Close();
    }

    [Fact]
    public void Refresh_WithUnchangedRows_DoesNotReset()
    {
        var a = MobileFixtures.Song("A");
        var alpha = MobileFixtures.MakeAlbum("Alpha", "X", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { alpha }, log: h => h.Seed(a, Now.AddHours(-1)));
        var resets = 0;
        rig.Shell.Home.LastPlayed.CollectionChanged += (_, _) => resets++;
        rig.Shell.Home.RecentlyPlayedAlbums.CollectionChanged += (_, _) => resets++;
        rig.Shell.Home.RecentlyAddedAlbums.CollectionChanged += (_, _) => resets++;

        rig.Shell.Home.Refresh();
        rig.Shell.SelectTabCommand.Execute(MobileTab.Home);

        Assert.Equal(0, resets);
    }

    [Fact]
    public void PlayLastPlayed_PlaysTheRowFromThatSong()
    {
        var a = MobileFixtures.Song("A");
        var b = MobileFixtures.Song("B");
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, log: h =>
        {
            h.Seed(a, Now.AddHours(-2));
            h.Seed(b, Now.AddHours(-1));
        });
        var home = rig.Shell.Home;

        home.PlayLastPlayedCommand.Execute(home.LastPlayed[1]);   // A, second newest

        Assert.Same(a, rig.Shell.Player.CurrentTrack);
    }
}

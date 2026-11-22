using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Home › Top Artists (beta testers 11-22 asked for "favorite artists"): the desktop Home's Top
/// Artists rule, the artists whose songs have the most counted plays (Track.PlayCount), as round
/// artist tiles; hidden until three artists have a play.
/// </summary>
public class MobileHomeTopArtistsTests
{
    private static Track Played(string title, string artist, int plays, string? cover = null)
    {
        var t = MobileFixtures.Song(title, artist: artist, plays: plays);
        t.AlbumArtworkPath = cover;
        return t;
    }

    [Fact]
    public void Rail_ShowsOnceThreeArtistsHaveAPlay_MostPlaysFirst_AddedUpPerArtist()
    {
        var a1 = Played("A1", "Ann", 2);
        var a2 = Played("A2", "Ann", 3);
        var b1 = Played("B1", "Bob", 4);
        var c1 = Played("C1", "Cat", 0);
        var u1 = Played("U1", "", 50);   // no artist tag: "Unknown Artist", never a top artist
        using var rig = MobileFixtures.MakeRig(new[] { a1, a2, b1, c1, u1 });
        var library = rig.Shell.Library;
        Assert.False(library.HasTopArtists);   // two artists played: not enough yet
        Assert.Empty(library.TopArtistsRail);

        c1.PlayCount = 1;
        rig.Library.RaiseLibraryUpdated();

        Assert.True(library.HasTopArtists);
        Assert.Equal(new[] { "Ann", "Bob", "Cat" }, library.TopArtistsRail.Select(r => r.Title));   // Ann 5, Bob 4, Cat 1
        Assert.All(library.TopArtistsRail, r => Assert.True(r.IsArtist));
    }

    [Fact]
    public void ATile_ShowsTheCoverOfTheArtistsMostPlayedSong()
    {
        using var rig = MobileFixtures.MakeRig(new[]
        {
            Played("A1", "Ann", 1, cover: "a1.jpg"), Played("A2", "Ann", 6, cover: "a2.jpg"), Played("A3", "Ann", 2),
            Played("B1", "Bob", 1, cover: "b1.jpg"), Played("C1", "Cat", 1),
        });
        var rail = rig.Shell.Library.TopArtistsRail;
        Assert.Equal("a2.jpg", rail.Single(r => r.Title == "Ann").ArtworkPath);
        Assert.Equal("b1.jpg", rail.Single(r => r.Title == "Bob").ArtworkPath);
        Assert.Null(rail.Single(r => r.Title == "Cat").ArtworkPath);
    }

    [Fact]
    public void Rail_HoldsTwelve_TheHeaderOpensThemAll_AndATileOpensTheArtist()
    {
        var songs = Enumerable.Range(1, 14).Select(i => Played("S" + i, "Artist " + i.ToString("00"), 20 - i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var shell = rig.Shell;
        Assert.Equal(12, shell.Library.TopArtistsRail.Count);
        Assert.Equal("Artist 01", shell.Library.TopArtistsRail[0].Title);

        shell.OpenTopArtistsCommand.Execute(null);
        var page = Assert.IsType<RailGridPageViewModel>(shell.CurrentPage);
        Assert.Equal("Top Artists", page.Title);
        Assert.Equal(12, page.Items.Count);

        shell.OpenRailItemCommand.Execute(page.Items[1]);
        Assert.Equal("Artist 02", shell.CurrentPage!.Title);
    }

    [AvaloniaFact]
    public void HomePage_ShowsTheSection_WithRoundTiles()
    {
        using var rig = MobileFixtures.MakeRig(new[] { Played("A", "Ann", 3), Played("B", "Bob", 2), Played("C", "Cat", 1) });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Assert.True(MobileFixtures.Named<StackPanel>(view, "TopArtistsSection").IsVisible);
        Assert.Equal(3, MobileFixtures.Named<ItemsControl>(view, "TopArtistsRail").ItemCount);
        window.Close();
    }
}

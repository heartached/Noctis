using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The artist photo shows wherever the phone shows an artist: the Artists grid (asked only for
/// the circles on screen, dropped when scrolled away), Search's artists, the pinned artists on
/// the Library tab, the artist sheet and the album page's artist link. Covers stand in until
/// a photo is on the phone, and without a photo source.
/// </summary>
public class MobileArtistPhotosTests
{
    private static MobileFixtures.Rig RigWithArtists(MobileFixtures.FakeArtistPhotos photos, params string[] names)
    {
        var rig = MobileFixtures.MakeRig(photos: photos);
        foreach (var name in names)
            rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = name, AlbumCount = 1, TrackCount = 3 });
        return rig;
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(i => $"Artist {i:D3}").ToArray();

    [AvaloniaFact]
    public void ArtistsGrid_AsksOnlyForTheCirclesOnScreen_AndDropsTheAsksScrolledAway()
    {
        var photos = new MobileFixtures.FakeArtistPhotos();
        var names = Names(90);
        foreach (var n in names) photos.Online[n] = $"/photos/{n}.jpg";
        using var rig = RigWithArtists(photos, names);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenArtistsCommand.Execute(null);
        window.UpdateLayout();

        var page = (ArtistListPageViewModel)rig.Shell.CurrentPage!;
        // A 915 dp screen shows about six rows of three: far fewer asks than artists.
        Assert.InRange(photos.Asked.Count, 9, 30);
        Assert.Equal(names.Take(photos.Asked.Count), photos.Asked);

        var scroll = MobileFixtures.Named<ScrollViewer>(MobileFixtures.Find<ArtistListPage>(view), "ArtistScroll");
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();

        Assert.Contains("Artist 000", photos.Cancelled);                  // the top rows left the screen
        Assert.Contains("Artist 089", photos.Asked);                      // the bottom rows came in
        Assert.DoesNotContain("Artist 045", photos.Asked);                // the middle was never shown

        photos.Complete();
        var last = page.Artists.Single(a => a.Name == "Artist 089");
        Assert.Equal("/photos/Artist 089.jpg", last.ArtworkPath);
        window.UpdateLayout();
        var circle = MobileFixtures.Find<ArtistListPage>(view).GetVisualDescendants().OfType<Button>()
            .First(b => b.DataContext is ArtistListItem item && item.Name == "Artist 089");
        Assert.Equal("/photos/Artist 089.jpg", circle.GetVisualDescendants().OfType<CachedImage>().Single().SourcePath);
        window.Close();
    }

    [Fact]
    public void ArtistsGrid_ShowsAPhotoAlreadyOnThePhone_AtOnce_AndTheCoverOtherwise()
    {
        var photos = new MobileFixtures.FakeArtistPhotos();
        photos.Cached["Known"] = "/photos/known.jpg";
        var song = MobileFixtures.Song("Song", artist: "Covered");
        var album = MobileFixtures.MakeAlbum("Record", "Covered", song);
        album.ArtworkPath = "/covers/record.jpg";
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album }, photos: photos);
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Known" });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Covered" });

        rig.Shell.OpenArtistsCommand.Execute(null);
        var page = (ArtistListPageViewModel)rig.Shell.CurrentPage!;

        Assert.Equal("/photos/known.jpg", page.Artists.Single(a => a.Name == "Known").ArtworkPath);
        Assert.Equal("/covers/record.jpg", page.Artists.Single(a => a.Name == "Covered").ArtworkPath);
        Assert.Empty(photos.Asked);   // nothing on screen without a view
    }

    [Fact]
    public void ArtistListItems_StayEqual_WhateverPhotoTheyCarry()
    {
        var artist = new Artist { Name = "Same" };
        var a = new ArtistListItem(artist, "/c.jpg");
        var b = new ArtistListItem(artist, "/c.jpg") { PhotoPath = "/p.jpg" };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal("/p.jpg", b.ArtworkPath);
        Assert.Equal("/c.jpg", a.ArtworkPath);
    }

    /// <summary>On the UI thread, as on the device: the asks resume there after the pause.</summary>
    [AvaloniaFact]
    public async Task Search_AsksForItsArtistResults_AfterAPause_AndANewSearchDropsTheOldAsks()
    {
        var photos = new MobileFixtures.FakeArtistPhotos();
        photos.Online["Bruno Mars"] = "/photos/bruno.jpg";
        using var rig = RigWithArtists(photos, "Bruno Mars", "Brandy", "Adele");

        rig.Shell.Search.Query = "br";
        Assert.Empty(photos.Asked);                                        // not on the keystroke itself
        await Task.Delay(SearchPageViewModel.PhotoDelay + TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { "Brandy", "Bruno Mars" }, photos.Asked.OrderBy(n => n));

        rig.Shell.Search.Query = "bru";
        Assert.Equal(new[] { "Brandy", "Bruno Mars" }, photos.Cancelled.OrderBy(n => n));

        photos.Cached["Bruno Mars"] = "/photos/bruno.jpg";
        rig.Shell.Search.Query = "brun";
        Assert.Equal("/photos/bruno.jpg", rig.Shell.Search.Artists.Single().ArtworkPath);   // on the phone: at once
    }

    [Fact]
    public async Task PinnedArtists_ShowTheirPhoto_AskedOnceASession()
    {
        var photos = new MobileFixtures.FakeArtistPhotos();
        photos.Online["Pinned"] = "/photos/pinned.jpg";
        var song = MobileFixtures.Song("Song", artist: "Pinned");
        var album = MobileFixtures.MakeAlbum("Record", "Pinned", song);
        album.ArtworkPath = "/covers/record.jpg";
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album }, photos: photos);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Pinned" };
        rig.Library.ArtistList.Add(artist);

        await rig.Shell.Library.SetArtistPinnedAsync("Pinned", true);

        var tile = rig.Shell.Library.PinnedRail.Single();
        Assert.Equal("/covers/record.jpg", tile.ArtworkPath);
        Assert.Equal(new[] { "Pinned" }, photos.Asked);

        photos.Complete();
        Assert.Equal("/photos/pinned.jpg", rig.Shell.Library.PinnedRail.Single().ArtworkPath);
        Assert.Single(photos.Asked);
    }

    [Fact]
    public void ArtistSheet_AndTheAlbumPagesArtistLink_UseThePhotoOnThePhone()
    {
        var photos = new MobileFixtures.FakeArtistPhotos();
        photos.Cached["Band"] = "/photos/band.jpg";
        var song = MobileFixtures.Song("Song", artist: "Band");
        var album = MobileFixtures.MakeAlbum("Record", "Band", song);
        album.ArtworkPath = "/covers/record.jpg";
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album }, photos: photos);
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Band" };

        rig.Shell.OpenArtistSheetCommand.Execute(new ArtistListItem(artist, "/covers/record.jpg"));
        Assert.Equal("/photos/band.jpg", rig.Shell.Sheet!.ArtworkPath);
        rig.Shell.CloseSheet();

        rig.Shell.OpenAlbumCommand.Execute(album);
        Assert.Equal("/photos/band.jpg", ((AlbumPageViewModel)rig.Shell.CurrentPage!).ArtistArtworkPath);
    }

    [Fact]
    public void WithoutAPhotoSource_CoversStandInEverywhere()
    {
        var song = MobileFixtures.Song("Song", artist: "Band");
        var album = MobileFixtures.MakeAlbum("Record", "Band", song);
        album.ArtworkPath = "/covers/record.jpg";
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        rig.Library.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = "Band" });

        rig.Shell.OpenArtistsCommand.Execute(null);
        Assert.Equal("/covers/record.jpg", ((ArtistListPageViewModel)rig.Shell.CurrentPage!).Artists.Single().ArtworkPath);
        rig.Shell.OpenArtistCommand.Execute("Band");
        var page = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Equal("/covers/record.jpg", page.HeroArtworkPath);
        Assert.False(page.HasPhoto);
    }
}

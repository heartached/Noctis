using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone artist page: featured album, top songs, discography, favourite, play/shuffle.</summary>
public class MobileArtistPageTests
{
    private static (Track[] Tracks, Album Old, Album New) Discography()
    {
        var a = MobileFixtures.Song("Early", artist: "Band", plays: 5);
        var b = MobileFixtures.Song("Hit", artist: "Band", plays: 9);
        var c = MobileFixtures.Song("Deep Cut", artist: "Band", plays: 1);
        var old = MobileFixtures.MakeAlbum("Debut", "Band", a);
        old.Year = 2010;
        var @new = MobileFixtures.MakeAlbum("Comeback", "Band", b, c);
        @new.Year = 2022;
        @new.ArtworkPath = "/art/comeback.jpg";
        foreach (var t in @new.Tracks) t.Year = 2022;
        return (new[] { a, b, c }, old, @new);
    }

    [AvaloniaFact]
    public void ArtistPage_FeaturesTheNewestAlbum_RanksTopSongsByPlays_AndListsTheDiscography()
    {
        var (tracks, old, @new) = Discography();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { old, @new });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenArtistCommand.Execute("Band");
        window.UpdateLayout();

        var vm = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.Same(@new, vm.FeaturedAlbum);
        Assert.Equal("2022 · 2 tracks", vm.FeaturedSubtitle);
        Assert.Equal(new[] { "Hit", "Early", "Deep Cut" }, vm.TopSongs.Select(s => s.Track.Title));
        Assert.Equal("Comeback · 2022", vm.TopSongs[0].Subtitle);
        Assert.Equal(new[] { "Comeback", "Debut" }, vm.Albums.Select(a => a.Name));
        Assert.Equal("/art/comeback.jpg", vm.HeroArtworkPath);

        var page = MobileFixtures.Find<ArtistPage>(view);
        Assert.Equal("Band", MobileFixtures.Named<TextBlock>(page, "ArtistName").Text);
        Assert.Equal("Comeback", MobileFixtures.Named<TextBlock>(page, "FeaturedTitle").Text);
        Assert.Equal(3, MobileFixtures.Named<ItemsControl>(page, "TopSongList").GetRealizedContainers().Count());
        window.Close();
    }

    [Fact]
    public void Favourite_PersistsInTheDesktopsFavouriteArtistsFile()
    {
        var (tracks, old, @new) = Discography();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { old, @new });
        rig.Shell.OpenArtistCommand.Execute("Band");
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.False(vm.IsFavourite);

        vm.ToggleFavouriteCommand.Execute(null);

        Assert.True(vm.IsFavourite);
        var reloaded = new FavoriteArtistsService(Path.Combine(rig.Persistence.DataDirectory, "favorite_artists.json"));
        Assert.True(reloaded.IsFavorite("band"));                  // case-insensitive, as on desktop
    }

    [Fact]
    public void PlayAndShuffle_QueueTheArtistsSongs_AndATopSongPlaysFromItsRank()
    {
        var (tracks, old, @new) = Discography();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { old, @new });
        rig.Shell.OpenArtistCommand.Execute("Band");
        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;

        vm.PlayCommand.Execute(null);
        Assert.Equal("Hit", rig.Shell.Player.CurrentTrack!.Title);   // newest album first, in track order
        Assert.Equal(new[] { "Deep Cut", "Early" }, rig.Shell.Player.UpNext.Select(t => t.Title));

        vm.PlayTopSongCommand.Execute(vm.TopSongs[1]);
        Assert.Equal("Early", rig.Shell.Player.CurrentTrack!.Title);
        Assert.Equal(new[] { "Deep Cut" }, rig.Shell.Player.UpNext.Select(t => t.Title));

        vm.ShuffleCommand.Execute(null);
        Assert.True(rig.Shell.Player.IsShuffleEnabled);
    }

    [AvaloniaFact]
    public void ArtistWithoutArtwork_ShowsThePlaceholderHero_AndNoTint()
    {
        var lone = MobileFixtures.Song("Lone", artist: "Solo");
        using var rig = MobileFixtures.MakeRig(new[] { lone });           // no album at all
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenArtistCommand.Execute("Solo");
        window.UpdateLayout();

        var vm = (ArtistPageViewModel)rig.Shell.CurrentPage!;
        Assert.Null(vm.HeroArtworkPath);
        Assert.False(vm.HasFeatured);
        Assert.False(vm.Tint.HasTint);
        Assert.Equal(new[] { "Lone" }, vm.TopSongs.Select(s => s.Track.Title));
        var page = MobileFixtures.Find<ArtistPage>(view);
        Assert.True(MobileFixtures.Named<PathIcon>(page, "HeroPlaceholder").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<Button>(page, "FeaturedCard").IsVisible);
        window.Close();
    }

    [Fact]
    public void SongsBy_IncludesAFeatureOnAnotherAlbum_Once()
    {
        var own = MobileFixtures.Song("Own", artist: "Guest");
        var feature = MobileFixtures.Song("Duet", artist: "Guest");
        var host = MobileFixtures.MakeAlbum("Host Album", "Host", feature);
        var mine = MobileFixtures.MakeAlbum("Guest Album", "Guest", own);
        using var rig = MobileFixtures.MakeRig(new[] { own, feature }, new[] { host, mine });

        Assert.Equal(new[] { "Own", "Duet" }, MobileLibrary.SongsBy(rig.Library, "Guest").Select(t => t.Title));
        Assert.Equal(new[] { "Duet" }, MobileLibrary.SongsBy(rig.Library, "Host").Select(t => t.Title));
    }

    [Fact]
    public void TheAlbumPagesArtistLink_OpensTheArtistPage()
    {
        var (tracks, old, @new) = Discography();
        using var rig = MobileFixtures.MakeRig(tracks, new[] { old, @new });
        rig.Shell.OpenAlbumCommand.Execute(old);
        ((AlbumPageViewModel)rig.Shell.CurrentPage!).OpenArtistCommand.Execute(null);

        var artist = Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Band", artist.Name);
    }
}
